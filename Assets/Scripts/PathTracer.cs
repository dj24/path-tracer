using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public enum SamplesPerPixel
{
    One = 1,
    Two = 2,
    Four = 4,
    Eight = 8,
    Sixteen = 16
}

public enum PathTracerKernel
{
    Mix,
    OneSample, 
    TwoSamples,
    FourSamples,
    EightSamples,
    SixteenSamples,
}

public class PathTracer : ScriptableRendererFeature
{
    private PathTracerPass _pathTracerPass;
    [Range(1,16)] public int downscaleFactor = 1;
    [Range(1,32)] public int maxBounces = 2;
    [Range(0.0f,1.0f)] public float materialFuzz;
    public SamplesPerPixel samplesPerPixel;
    public bool useAccumulation;
    class PathTracerPass : ScriptableRenderPass
    {
        private RenderTargetIdentifier _colorBuffer, _depthBuffer;
        private const string ProfilerTag = "PathTracerPass";
        private readonly ComputeShader _pathTraceComputeShader, _triangleBufferComputeShader;
        private readonly int _downscaleFactor, _maxBounces;
        private readonly float _blendAmount, _materialFuzz;
        private float _verticalFov;
        private Vector3 _cameraDirection;
        private readonly int _samplesPerPixel;
        private readonly bool _useAccumulation;
        private readonly int _vertexStride;

        private int _kernelIndex
        {
            get
            {
                switch (_samplesPerPixel)
                {
                    case (int)SamplesPerPixel.Two:
                        return (int) PathTracerKernel.TwoSamples;
                    case (int)SamplesPerPixel.Four:
                        return (int) PathTracerKernel.FourSamples;
                    case (int)SamplesPerPixel.Eight:
                        return (int) PathTracerKernel.EightSamples;
                    case (int)SamplesPerPixel.Sixteen:
                        return (int) PathTracerKernel.SixteenSamples;
                    default:
                        return (int) PathTracerKernel.OneSample;
                }
            }
        }

        public PathTracerPass(int downscaleFactor, int samplesPerPixel, int maxBounces, bool useAccumulation, float materialFuzz)
        {
            _maxBounces = maxBounces;
            _materialFuzz = materialFuzz;
            _useAccumulation = useAccumulation;
            _samplesPerPixel = samplesPerPixel;
            _downscaleFactor = downscaleFactor == 0 ? 1 : downscaleFactor;
            _pathTraceComputeShader = (ComputeShader)Resources.Load("Compute/PathTracer");
            _triangleBufferComputeShader = (ComputeShader)Resources.Load("Compute/FormatTriangleBuffer");
        }
        
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            _verticalFov = renderingData.cameraData.camera.fieldOfView;
            _cameraDirection = -renderingData.cameraData.camera.transform.forward;
            _colorBuffer = renderingData.cameraData.renderer.cameraColorTarget;
            _depthBuffer = renderingData.cameraData.renderer.cameraDepthTarget;
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (renderingData.cameraData.cameraType == CameraType.Preview ||
                renderingData.cameraData.cameraType == CameraType.Reflection)
            {
                return;
            }

            var reflectionProbe = Camera.main.GetComponent<ReflectionProbe>();
            if (reflectionProbe.texture == null)
            {
                return;
            }
            var meshFilters = FindObjectsOfType(typeof(MeshFilter)) as MeshFilter[];
            if (meshFilters.Length == 0)
            {
                return;
            }

            var sceneTriangleCount = 0;
            
            // Get triangle count to initialise triangle buffer
            foreach (var meshFilter in meshFilters)
            {
                var mesh = meshFilter.sharedMesh;
                var meshTriangleCount = (int)(mesh.GetIndexCount(0) / 3);
                sceneTriangleCount += meshTriangleCount;
            }
            
            var indexBuffers = new List<GraphicsBuffer>();
            var vertexBuffers = new List<GraphicsBuffer>();
            var sceneTriangles = new ComputeBuffer(sceneTriangleCount, 36);
            var currentOffset = 0;
            
            // Add all meshes to one triangle buffer
            for (int i = 0; i < meshFilters.Length; i++)
            {
                var mesh = meshFilters[i].sharedMesh;
                mesh.indexBufferTarget = GraphicsBuffer.Target.Raw;
                mesh.vertexBufferTarget = GraphicsBuffer.Target.Raw;
                var vertexBuffer = mesh.GetVertexBuffer(0);
                // TODO: add normals for interpolation
                // var normalBuffer = 
                var indexBuffer = mesh.GetIndexBuffer();
                vertexBuffers.Add(vertexBuffer);
                indexBuffers.Add(indexBuffer);
                var meshTriangleCount = (int)(mesh.GetIndexCount(0) / 3);
                _triangleBufferComputeShader.SetBuffer(0, "VertexBuffer", vertexBuffer);
                _triangleBufferComputeShader.SetBuffer(0, "IndexBuffer", indexBuffer);
                _triangleBufferComputeShader.SetBuffer(0, "SceneTriangles", sceneTriangles);
                _triangleBufferComputeShader.SetMatrix("LocalToWorld", meshFilters[i].GetComponent<Renderer>().localToWorldMatrix);
                _triangleBufferComputeShader.SetInt("VertexStride", vertexBuffer.stride);
                _triangleBufferComputeShader.SetInt("Offset", currentOffset);
                _triangleBufferComputeShader.Dispatch(0, meshTriangleCount, 1, 1);
                AsyncGPUReadback.Request(sceneTriangles).WaitForCompletion();
                currentOffset += meshTriangleCount;
            }

            // Once all triangles are added, dispose each meshes buffers
            foreach (var buffer in indexBuffers)
            {
                buffer.Dispose();
            }
            foreach (var buffer in vertexBuffers)
            {
                buffer.Dispose();
            }
            
            // Init input and output texture
            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            var _sceneTexture = RenderTexture.GetTemporary(descriptor);
            var _depthTexture = RenderTexture.GetTemporary(descriptor);
            var _upscaleTexture = RenderTexture.GetTemporary(descriptor);
            descriptor.enableRandomWrite = true;
            var _outputTexture = RenderTexture.GetTemporary(descriptor);
            descriptor.width /= _downscaleFactor;
            descriptor.height /= _downscaleFactor;
            var _downscaleTexture = RenderTexture.GetTemporary(descriptor);
            
            // Copy scene buffer into texture
            var cmd = CommandBufferPool.Get(ProfilerTag);
            cmd.Blit(_depthBuffer,_depthTexture);
            cmd.Blit(_colorBuffer,_sceneTexture);

            // Execute compute
            _pathTraceComputeShader.SetBuffer(_kernelIndex,"SceneTriangles", sceneTriangles);
            _pathTraceComputeShader.SetInt("TriangleCount", sceneTriangles.count);
            _pathTraceComputeShader.SetBool("UseAccumulation", _useAccumulation);
            _pathTraceComputeShader.SetInt("Width", _downscaleTexture.width);
            _pathTraceComputeShader.SetVector("MeshOrigin", meshFilters[0].transform.position);
            _pathTraceComputeShader.SetInt("Height", _downscaleTexture.height);
            _pathTraceComputeShader.SetInt("DownscaleFactor", _downscaleFactor);
            _pathTraceComputeShader.SetFloat("MaterialFuzz", _materialFuzz);
            _pathTraceComputeShader.SetInt("MaxBounces", _maxBounces);
            _pathTraceComputeShader.SetInt("SamplesPerPixel", _samplesPerPixel);
            _pathTraceComputeShader.SetFloat("VerticalFov", _verticalFov);
            _pathTraceComputeShader.SetVector("CameraDirection", new Vector4(_cameraDirection.x, _cameraDirection.y, _cameraDirection.z, 0));
            _pathTraceComputeShader.SetTexture(_kernelIndex, Shader.PropertyToID("Downscale"), _downscaleTexture);
            _pathTraceComputeShader.SetTexture(_kernelIndex, Shader.PropertyToID("Depth"), _depthTexture);
            _pathTraceComputeShader.SetTexture(_kernelIndex, Shader.PropertyToID("Skybox"), reflectionProbe.texture);
            
            int threadGroupsX = _downscaleTexture.width; 
            int threadGroupsY = _downscaleTexture.height;
            _pathTraceComputeShader.Dispatch(_kernelIndex, threadGroupsX, threadGroupsY, 1);
            
            AsyncGPUReadback.Request(_outputTexture).WaitForCompletion();
            
            // Clean up
            RenderTexture.ReleaseTemporary(_sceneTexture);
            RenderTexture.ReleaseTemporary(_downscaleTexture);
            RenderTexture.ReleaseTemporary(_depthTexture);
            sceneTriangles.Release();
            
            cmd.Blit(_downscaleTexture,_upscaleTexture);

            _pathTraceComputeShader.SetTexture((int)PathTracerKernel.Mix, Shader.PropertyToID("Result"), _outputTexture);
            _pathTraceComputeShader.SetTexture((int)PathTracerKernel.Mix, Shader.PropertyToID("Upscale"), _upscaleTexture);
            _pathTraceComputeShader.SetTexture((int)PathTracerKernel.Mix, Shader.PropertyToID("Scene"), _sceneTexture);
            _pathTraceComputeShader.Dispatch((int)PathTracerKernel.Mix, _sceneTexture.width, _sceneTexture.height, 1);
            
            // Sync compute with frame
            AsyncGPUReadback.Request(_outputTexture).WaitForCompletion();


            // Clean up
            RenderTexture.ReleaseTemporary(_outputTexture);
            RenderTexture.ReleaseTemporary(_upscaleTexture);
            
            // Copy processed texture into scene buffer
            cmd.Blit(_outputTexture,_colorBuffer);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    public override void Create()
    {
        _pathTracerPass = new PathTracerPass(downscaleFactor, (int)samplesPerPixel, maxBounces, useAccumulation, materialFuzz);
        _pathTracerPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_pathTracerPass);
    }
}
