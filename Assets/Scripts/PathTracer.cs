using System.Collections.Generic;
using System.Linq;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public enum InterpolationType
{
    NearestNeighbour,
    Bilinear,
    BicubicHermite,
    Lanczos
}

public class PathTracer : ScriptableRendererFeature
{
    private PathTracerPass _pathTracerPass;
    [Range(2,16)] public int downscaleFactor = 2;
    [Range(1,32)] public int maxBounces = 2;
    [Range(0,32)] public int blurSamples = 0;
    [Range(0.0f,1.0f)] public float materialFuzz;
    public Color materialColor;
    public bool useAccumulation, materialIsMetal;
    public InterpolationType interpolationType;
    class PathTracerPass : ScriptableRenderPass
    {
        private RenderTargetIdentifier _colorBuffer, _depthBuffer;
        private const string ProfilerTag = "PathTracerPass";
        private readonly ComputeShader _pathTraceComputeShader, _triangleBufferComputeShader, _mixComputeShader;
        private readonly Shader _pathTraceSurfaceShader;
        private readonly int _downscaleFactor, _maxBounces, _blurSamples;
        private readonly float _blendAmount, _materialFuzz;
        private float _verticalFov;
        private Vector3 _cameraDirection;
        private readonly bool _useAccumulation, _materialIsMetal;
        private readonly int _vertexStride;
        private Color _materialColor;
        private InterpolationType _interpolationType;

        private int _traceKernelIndex => _pathTraceComputeShader.FindKernel("Trace");
        private int _mixKernelIndex => _mixComputeShader.FindKernel("Mix");
        private int _horizontalBlurKernelIndex => _mixComputeShader.FindKernel("HorizontalBlur");
        private int _verticalBlurKernelIndex => _mixComputeShader.FindKernel("VerticalBlur");

        public PathTracerPass(int downscaleFactor, int maxBounces, int blurSamples, bool useAccumulation, float materialFuzz, bool materialIsMetal, Color materialColor, InterpolationType interpolationType)
        {
            _interpolationType = interpolationType;
            _blurSamples = blurSamples;
            _materialColor = materialColor;
            _materialIsMetal = materialIsMetal;
            _maxBounces = maxBounces;
            _materialFuzz = materialFuzz;
            _useAccumulation = useAccumulation;
            _downscaleFactor = downscaleFactor == 0 ? 1 : downscaleFactor;
            _pathTraceComputeShader = (ComputeShader)Resources.Load("Compute/PathTracer");
            _mixComputeShader = (ComputeShader)Resources.Load("Compute/Mix");
            _triangleBufferComputeShader = (ComputeShader)Resources.Load("Compute/FormatTriangleBuffer");
            _pathTraceSurfaceShader = Resources.Load("Shaders/PathTraceSurface", typeof(Shader)) as Shader;
        }
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            renderingData.cameraData.camera.depthTextureMode = DepthTextureMode.MotionVectors | DepthTextureMode.Depth;
            ConfigureInput(ScriptableRenderPassInput.Depth);
            ConfigureInput(ScriptableRenderPassInput.Motion);
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
            
            var meshFilters = FindObjectsOfType(typeof(MeshFilter)) as MeshFilter[];
            if (meshFilters.Length == 0)
            {
                return;
            }

            var sceneTriangleCount = 0;
            
            // Get triangle count to initialise triangle buffer
            foreach (var meshFilter in meshFilters)
            {
                // foreach (var sharedMaterial in meshFilter.transform.GetComponent<MeshRenderer>().sharedMaterials)
                // {
                //     if (sharedMaterial.shader == _pathTraceSurfaceShader)
                //     {
                        var mesh = meshFilter.sharedMesh;
                        var meshTriangleCount = (int)(mesh.GetIndexCount(0) / 3);
                        sceneTriangleCount += meshTriangleCount;
                //     }
                // }
            }
            
            var meshBuffers = new List<GraphicsBuffer>();
            var sceneTriangles = new ComputeBuffer(sceneTriangleCount, 72);
            var currentOffset = 0;
            
            // Add all meshes to one triangle buffer
            for (int i = 0; i < meshFilters.Length; i++)
            {
                var mesh = meshFilters[i].sharedMesh;
                mesh.indexBufferTarget = GraphicsBuffer.Target.Raw;
                mesh.vertexBufferTarget = GraphicsBuffer.Target.Raw;
                var vertexBuffer = mesh.GetVertexBuffer(0);
                var indexBuffer = mesh.GetIndexBuffer();
                meshBuffers.Add(vertexBuffer);
                meshBuffers.Add(indexBuffer);
                
                var meshTriangleCount = (int)(mesh.GetIndexCount(0) / 3);
                _triangleBufferComputeShader.SetInt("PositionOffset", mesh.GetVertexAttributeOffset(VertexAttribute.Position));
                _triangleBufferComputeShader.SetInt("NormalOffset", mesh.GetVertexAttributeOffset(VertexAttribute.Normal));
                _triangleBufferComputeShader.SetBuffer(0, "VertexBuffer", vertexBuffer);
                _triangleBufferComputeShader.SetBuffer(0, "IndexBuffer", indexBuffer);
                _triangleBufferComputeShader.SetBuffer(0, "SceneTriangles", sceneTriangles);
                _triangleBufferComputeShader.SetVector("CameraDirection", new Vector4(_cameraDirection.x, _cameraDirection.y, _cameraDirection.z, 0));
                _triangleBufferComputeShader.SetMatrix("LocalToWorld", meshFilters[i].GetComponent<Renderer>().localToWorldMatrix);
                _triangleBufferComputeShader.SetInt("VertexStride", vertexBuffer.stride);
                _triangleBufferComputeShader.SetInt("Offset", currentOffset);
                _triangleBufferComputeShader.GetKernelThreadGroupSizes(0,out var groupSize, out var _, out _);
                int threadGroups = (int) Mathf.Ceil(meshTriangleCount / (float)groupSize); 
                _triangleBufferComputeShader.Dispatch(0, threadGroups, 1, 1);
                currentOffset += meshTriangleCount;
            }
            
            // Once all triangles are added, dispose each meshes buffers
            foreach (var buffer in meshBuffers)
            {
                buffer.Dispose();
            }
            
            // Init input and output texture
            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            var _sceneTexture = RenderTexture.GetTemporary(descriptor);
            var _depthTexture = RenderTexture.GetTemporary(descriptor);
            descriptor.enableRandomWrite = true;
            var _outputTexture = RenderTexture.GetTemporary(descriptor);
            Shader.SetGlobalTexture("_PathTraceTexture", _outputTexture);
            Shader.SetGlobalFloat("_PathTraceDownscaleFactor", _downscaleFactor);
            descriptor.width /= _downscaleFactor;
            descriptor.height /= _downscaleFactor;
            var _downscaleTexture = RenderTexture.GetTemporary(descriptor);
            
            // Copy scene buffer into texture
            var cmd = CommandBufferPool.Get(ProfilerTag);
            cmd.Blit(_depthBuffer,_depthTexture);
            cmd.Blit(_colorBuffer,_sceneTexture);

            var samplePosisions = new Vector4[]
            {
                new(0.0625f,0.0625f, 0f, 0f),
                new(-0.0625f,-0.1875f, 0f, 0f),
                new(-0.1875f,0.125f, 0f, 0f),
                new(0.25f,-0.0625f, 0f, 0f),
                new(-0.3125f,-0.125f, 0f, 0f),
                new(0.125f,0.3125f, 0f, 0f),
                new(0.3125f,0.1875f, 0f, 0f),
                new(0.1875f,-0.3125f, 0f, 0f),
                new(-0.125f,0.375f, 0f, 0f),
                new(0f,-0.4375f, 0f, 0f),
                new(-0.25f,-0.375f, 0f, 0f),
                new(-0.375f,0.25f, 0f, 0f),
                new(-0.5f,0f, 0f, 0f),
                new(0.4375f,-0.25f, 0f, 0f),
                new(0.375f,0.4375f, 0f, 0f),
                new(-0.4375f,-0.5f, 0f, 0f),
            };

            // Execute compute
            var motionVectors = Shader.GetGlobalTexture("_MotionVectorTexture");
            var isUsingCubeSky = RenderSettings.skybox.shader == Shader.Find("Skybox/Cubemap");

            _pathTraceComputeShader.SetInt(Shader.PropertyToID("FrameNumber"), Time.frameCount % samplePosisions.Length);
            _pathTraceComputeShader.SetVectorArray(Shader.PropertyToID("SamplePositions"), samplePosisions);
            _pathTraceComputeShader.SetBuffer(_traceKernelIndex,"SceneTriangles", sceneTriangles);
            _pathTraceComputeShader.SetInt("TriangleCount", sceneTriangles.count);
            _pathTraceComputeShader.SetBool("UseAccumulation", _useAccumulation);
            _pathTraceComputeShader.SetInt("Width", _downscaleTexture.width);
            _pathTraceComputeShader.SetVector("MeshOrigin", meshFilters[0].transform.position);
            _pathTraceComputeShader.SetInt("Height", _downscaleTexture.height);
            _pathTraceComputeShader.SetInt("DownscaleFactor", _downscaleFactor);
            _pathTraceComputeShader.SetFloat("MaterialFuzz", _materialFuzz);
            _pathTraceComputeShader.SetInt("MaxBounces", _maxBounces);
            _pathTraceComputeShader.SetBool("IsMetal", _materialIsMetal);
            _pathTraceComputeShader.SetVector("Color", _materialColor);
            _pathTraceComputeShader.SetFloat("VerticalFov", _verticalFov);
            _pathTraceComputeShader.SetVector("CameraDirection", new Vector4(_cameraDirection.x, _cameraDirection.y, _cameraDirection.z, 0));
            _pathTraceComputeShader.SetTexture(_traceKernelIndex, Shader.PropertyToID("Motion"), motionVectors != null ? motionVectors : Texture2D.blackTexture);
            _pathTraceComputeShader.SetTexture(_traceKernelIndex, Shader.PropertyToID("Downscale"), _downscaleTexture);
            _pathTraceComputeShader.SetTexture(_traceKernelIndex, Shader.PropertyToID("Depth"), _depthTexture);
            _pathTraceComputeShader.SetTexture(_traceKernelIndex, Shader.PropertyToID("Skybox"), isUsingCubeSky ? RenderSettings.skybox.GetTexture("_Tex") : Texture2D.blackTexture);

            uint groupSizeX;
            uint groupSizeY;

            _pathTraceComputeShader.GetKernelThreadGroupSizes(_traceKernelIndex,out groupSizeX, out groupSizeY, out _);
            int threadGroupsX = (int) Mathf.Ceil(_downscaleTexture.width / (float)groupSizeX); 
            int threadGroupsY = (int) Mathf.Ceil(_downscaleTexture.height / (float)groupSizeY);
            _pathTraceComputeShader.Dispatch(_traceKernelIndex, threadGroupsX, threadGroupsY, 1);
            
            sceneTriangles.Release();
            RenderTexture.ReleaseTemporary(_depthTexture);

            _mixComputeShader.SetInt(Shader.PropertyToID("InterpolationType"), (int)_interpolationType);
            _mixComputeShader.SetFloat(Shader.PropertyToID("Samples"), _blurSamples);
            _mixComputeShader.SetInt(Shader.PropertyToID("DownscaleFactor"), _downscaleFactor);
            _mixComputeShader.SetTexture(_mixKernelIndex, Shader.PropertyToID("Result"), _outputTexture);
            _mixComputeShader.SetTexture(_mixKernelIndex, Shader.PropertyToID("Downscale"), _downscaleTexture);
            _mixComputeShader.SetTexture(_mixKernelIndex, Shader.PropertyToID("Motion"), motionVectors != null ? motionVectors : Texture2D.blackTexture);
            _mixComputeShader.SetTexture(_horizontalBlurKernelIndex, Shader.PropertyToID("Downscale"), _downscaleTexture);
            _mixComputeShader.SetTexture(_verticalBlurKernelIndex, Shader.PropertyToID("Downscale"), _downscaleTexture);
            _mixComputeShader.SetTexture(_mixKernelIndex, Shader.PropertyToID("Scene"), _sceneTexture);
            
            if (_blurSamples > 0)
            {
                _mixComputeShader.Dispatch(_horizontalBlurKernelIndex,_downscaleTexture.width, _downscaleTexture.height, 1);
                _mixComputeShader.Dispatch(_verticalBlurKernelIndex,_downscaleTexture.width, _downscaleTexture.height, 1);
            }
            _mixComputeShader.GetKernelThreadGroupSizes(_traceKernelIndex,out groupSizeX, out groupSizeY, out _);
            threadGroupsX = (int) Mathf.Ceil(_downscaleTexture.width / (float)groupSizeX); 
            threadGroupsY = (int) Mathf.Ceil(_downscaleTexture.height / (float)groupSizeY);
            _mixComputeShader.Dispatch(_mixKernelIndex, threadGroupsX, threadGroupsY, 1);
            
            // Sync compute with frame
            AsyncGPUReadback.Request(_outputTexture).WaitForCompletion();
            
            // Copy processed texture into scene buffer
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            
            // Clean up
            RenderTexture.ReleaseTemporary(_sceneTexture);
            RenderTexture.ReleaseTemporary(_outputTexture);
            RenderTexture.ReleaseTemporary(_downscaleTexture);
        }
    }

    public override void Create()
    {
        _pathTracerPass = new PathTracerPass(downscaleFactor, maxBounces, blurSamples,useAccumulation, materialFuzz, materialIsMetal, materialColor, interpolationType);
        _pathTracerPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_pathTracerPass);
    }
}
