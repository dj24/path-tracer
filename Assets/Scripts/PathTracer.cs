using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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

public struct MeshDescriptor
{
    public int startIndex;
    public int endIndex;
}

public class PathTracer : ScriptableRendererFeature
{
    private PathTracerPass _pathTracerPass;
    [Range(2,16)] public int downscaleFactor = 2;
    [Range(1,32)] public int maxBounces = 2;
    [Range(0.0f,1.0f)] public float materialFuzz;
    public Color materialColor;
    public bool useAccumulation, materialIsMetal;
    public InterpolationType interpolationType;
    private static readonly int Tex = Shader.PropertyToID("_Tex");

    class PathTracerPass : ScriptableRenderPass
    {
        private RenderTargetIdentifier _colorBuffer, _depthBuffer;
        private const string ProfilerTag = "PathTracerPass";
        private readonly ComputeShader _pathTraceComputeShader, _triangleBufferComputeShader, _mixComputeShader;
        private readonly Shader _pathTraceSurfaceShader;
        private readonly int _downscaleFactor, _maxBounces;
        private readonly float _blendAmount, _materialFuzz;
        private float _verticalFov;
        private Vector3 _cameraDirection;
        private readonly bool _useAccumulation, _materialIsMetal;
        private readonly int _vertexStride;
        private readonly Color _materialColor;
        private readonly InterpolationType _interpolationType;

        private int _traceKernelIndex => _pathTraceComputeShader.FindKernel("Trace");
        public PathTracerPass(int downscaleFactor, int maxBounces, bool useAccumulation, float materialFuzz, bool materialIsMetal, Color materialColor, InterpolationType interpolationType)
        {
            renderPassEvent = RenderPassEvent.BeforeRendering;
            _interpolationType = interpolationType;
            _materialColor = materialColor;
            _materialIsMetal = materialIsMetal;
            _maxBounces = maxBounces;
            _materialFuzz = materialFuzz;
            _useAccumulation = useAccumulation;
            _downscaleFactor = downscaleFactor == 0 ? 1 : downscaleFactor;
            _pathTraceComputeShader = (ComputeShader)Resources.Load("Compute/PathTracer");
            _triangleBufferComputeShader = (ComputeShader)Resources.Load("Compute/FormatTriangleBuffer");
            _pathTraceSurfaceShader = Resources.Load("Shaders/PathTraceSurface", typeof(Shader)) as Shader;
        }
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            renderingData.cameraData.camera.depthTextureMode = DepthTextureMode.MotionVectors | DepthTextureMode.Depth | DepthTextureMode.DepthNormals;
            ConfigureInput(ScriptableRenderPassInput.Depth);
            ConfigureInput(ScriptableRenderPassInput.Normal);
            ConfigureInput(ScriptableRenderPassInput.Motion);
            _verticalFov = renderingData.cameraData.camera.fieldOfView;
            _cameraDirection = -renderingData.cameraData.camera.transform.forward;
            _colorBuffer = renderingData.cameraData.renderer.cameraColorTarget;
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var meshFilters = FindObjectsOfType(typeof(MeshFilter)) as MeshFilter[];
            if (renderingData.cameraData.cameraType == CameraType.Preview ||
                renderingData.cameraData.cameraType == CameraType.Reflection ||
                meshFilters.Length == 0)
            {
                return;
            }
            
            var cmd = CommandBufferPool.Get(ProfilerTag);
            var sceneTriangleCount = 0;
            
            // Get triangle count to initialise triangle buffer
            foreach (var meshFilter in meshFilters)
            {
                var mesh = meshFilter.sharedMesh;
                var meshTriangleCount = (int)(mesh.GetIndexCount(0) / 3);
                sceneTriangleCount += meshTriangleCount;
            }
            
            var meshBuffers = new List<GraphicsBuffer>();
            var sceneTriangles = new ComputeBuffer(sceneTriangleCount, 72);
            var meshTriangleOffsets = new List<MeshDescriptor>();
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
                _triangleBufferComputeShader.SetInt("VertexStride", vertexBuffer.stride);
                _triangleBufferComputeShader.SetInt("Offset", currentOffset);
                _triangleBufferComputeShader.SetBuffer(0, "VertexBuffer", vertexBuffer);
                _triangleBufferComputeShader.SetBuffer(0, "IndexBuffer", indexBuffer);
                _triangleBufferComputeShader.SetBuffer(0, "SceneTriangles", sceneTriangles);
                _triangleBufferComputeShader.SetVector("CameraDirection", new Vector4(_cameraDirection.x, _cameraDirection.y, _cameraDirection.z, 0));
                _triangleBufferComputeShader.SetMatrix("LocalToWorld", meshFilters[i].GetComponent<Renderer>().localToWorldMatrix);
                _triangleBufferComputeShader.GetKernelThreadGroupSizes(0,out var groupSize, out var _, out _);
                int threadGroups = (int) Mathf.Ceil(meshTriangleCount / (float)groupSize); 
                _triangleBufferComputeShader.Dispatch(0, threadGroups, 1, 1);
                meshTriangleOffsets.Add(new MeshDescriptor{startIndex = currentOffset, endIndex = currentOffset + meshTriangleCount});
                currentOffset += meshTriangleCount;
            }
            
            // Once all triangles are added, dispose each meshes buffers
            foreach (var buffer in meshBuffers)
            {
                buffer.Dispose();
            }
            
            // Init input and output texture
            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            var motionVectors = Shader.GetGlobalTexture("_MotionVectorTexture");
            var depthTexture = Shader.GetGlobalTexture("_CameraDepthTexture");
            
            Shader.SetGlobalFloat("_PathTraceDownscaleFactor", _downscaleFactor);
            Shader.SetGlobalInt("_PathTraceInterpolationType", (int)_interpolationType);
            descriptor.width /= _downscaleFactor;
            descriptor.height /= _downscaleFactor;
            descriptor.enableRandomWrite = true;
            var _downscaleTexture = RenderTexture.GetTemporary(descriptor);
            
            
            
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
            
            var isUsingCubeSky = RenderSettings.skybox.shader == Shader.Find("Skybox/Cubemap");
            var meshTriangleOffsetsBuffer = new ComputeBuffer(meshTriangleOffsets.Count, Marshal.SizeOf(typeof(MeshDescriptor)));
            meshTriangleOffsetsBuffer.SetData(meshTriangleOffsets);

            // Execute compute
            _pathTraceComputeShader.SetBool("UseAccumulation", _useAccumulation);
            _pathTraceComputeShader.SetBool("IsMetal", _materialIsMetal);
            _pathTraceComputeShader.SetInt(Shader.PropertyToID("FrameNumber"), Time.frameCount % 256);
            _pathTraceComputeShader.SetInt("TriangleCount", sceneTriangles.count);
            _pathTraceComputeShader.SetInt("Width", _downscaleTexture.width);
            _pathTraceComputeShader.SetInt("Height", _downscaleTexture.height);
            _pathTraceComputeShader.SetInt("DownscaleFactor", _downscaleFactor);
            _pathTraceComputeShader.SetInt("MaxBounces", _maxBounces);
            _pathTraceComputeShader.SetInt("MeshCount", meshTriangleOffsets.Count);
            _pathTraceComputeShader.SetFloat("VerticalFov", _verticalFov);
            _pathTraceComputeShader.SetFloat("MaterialFuzz", _materialFuzz);
            _pathTraceComputeShader.SetVector("MeshOrigin", meshFilters[0].transform.position);
            _pathTraceComputeShader.SetVector("Color", _materialColor);
            _pathTraceComputeShader.SetVector("CameraDirection", new Vector4(_cameraDirection.x, _cameraDirection.y, _cameraDirection.z, 0));
            _pathTraceComputeShader.SetVectorArray(Shader.PropertyToID("SamplePositions"), samplePosisions);
            _pathTraceComputeShader.SetBuffer(_traceKernelIndex,"SceneTriangles", sceneTriangles);
            _pathTraceComputeShader.SetBuffer(_traceKernelIndex, "MeshTrianglesOffsets", meshTriangleOffsetsBuffer);
            _pathTraceComputeShader.SetTexture(_traceKernelIndex, Shader.PropertyToID("Motion"), motionVectors != null ? motionVectors : Texture2D.blackTexture);
            _pathTraceComputeShader.SetTexture(_traceKernelIndex, Shader.PropertyToID("Downscale"), _downscaleTexture);
            _pathTraceComputeShader.SetTexture(_traceKernelIndex, Shader.PropertyToID("Depth"), depthTexture != null ? depthTexture : Texture2D.blackTexture);
            _pathTraceComputeShader.SetTexture(_traceKernelIndex, Shader.PropertyToID("Skybox"), isUsingCubeSky ? RenderSettings.skybox.GetTexture(Tex) : Texture2D.blackTexture);

            
            // cmd.SetComputeIntParam(_pathTraceComputeShader, "UseAccumulation", _useAccumulation ? 1: 0);
            // cmd.SetComputeIntParam(_pathTraceComputeShader, "IsMetal", _materialIsMetal ? 1: 0);
            // cmd.SetComputeIntParam(_pathTraceComputeShader, Shader.PropertyToID("FrameNumber"), Time.frameCount % 256);
            // cmd.SetComputeIntParam(_pathTraceComputeShader, "TriangleCount", sceneTriangles.count);
            // cmd.SetComputeIntParam(_pathTraceComputeShader, "Width", _downscaleTexture.width);
            // cmd.SetComputeIntParam(_pathTraceComputeShader, "Height", _downscaleTexture.height);
            // cmd.SetComputeIntParam(_pathTraceComputeShader, "DownscaleFactor", _downscaleFactor);
            // cmd.SetComputeIntParam(_pathTraceComputeShader, "MaxBounces", _maxBounces);
            // cmd.SetComputeIntParam(_pathTraceComputeShader, "MeshCount", meshTriangleOffsets.Count);
            // cmd.SetComputeFloatParam(_pathTraceComputeShader, "VerticalFov", _verticalFov);
            // cmd.SetComputeFloatParam(_pathTraceComputeShader, "MaterialFuzz", _materialFuzz);
            // cmd.SetComputeVectorParam(_pathTraceComputeShader, "MeshOrigin", meshFilters[0].transform.position);
            // cmd.SetComputeVectorParam(_pathTraceComputeShader, "Color", _materialColor);
            // cmd.SetComputeVectorParam(_pathTraceComputeShader, "CameraDirection", new Vector4(_cameraDirection.x, _cameraDirection.y, _cameraDirection.z, 0));
            // cmd.SetComputeVectorArrayParam(_pathTraceComputeShader, Shader.PropertyToID("SamplePositions"), samplePosisions);
            // cmd.SetComputeBufferParam(_pathTraceComputeShader, _traceKernelIndex,"SceneTriangles", sceneTriangles);
            // cmd.SetComputeBufferParam(_pathTraceComputeShader, _traceKernelIndex, "MeshTrianglesOffsets", meshTriangleOffsetsBuffer);
            // cmd.SetComputeTextureParam(_pathTraceComputeShader, _traceKernelIndex, Shader.PropertyToID("Motion"), motionVectors != null ? motionVectors : Texture2D.blackTexture);
            // cmd.SetComputeTextureParam(_pathTraceComputeShader, _traceKernelIndex, Shader.PropertyToID("Downscale"), _downscaleTexture);
            // cmd.SetComputeTextureParam(_pathTraceComputeShader, _traceKernelIndex, Shader.PropertyToID("Depth"), tempDepth);
            // cmd.SetComputeTextureParam(_pathTraceComputeShader, _traceKernelIndex, Shader.PropertyToID("Skybox"), isUsingCubeSky ? RenderSettings.skybox.GetTexture(Tex) : Texture2D.blackTexture);

            _pathTraceComputeShader.GetKernelThreadGroupSizes(_traceKernelIndex,out uint groupSizeX, out uint groupSizeY, out uint _);
            int threadGroupsX = (int) Mathf.Ceil(_downscaleTexture.width / (float)groupSizeX); 
            int threadGroupsY = (int) Mathf.Ceil(_downscaleTexture.height / (float)groupSizeY);
            _pathTraceComputeShader.Dispatch(_traceKernelIndex, threadGroupsX, threadGroupsY, 1);
            
            meshTriangleOffsetsBuffer.Release();
            sceneTriangles.Release();
            
            // Sync compute with frame
            AsyncGPUReadback.Request(_downscaleTexture).WaitForCompletion();
            Shader.SetGlobalTexture("_PathTraceTexture", _downscaleTexture);
            // cmd.Blit(_colorBuffer, _downscaleTexture);

            // Clean up
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            RenderTexture.ReleaseTemporary(_downscaleTexture);
        }
    }

    public override void Create()
    {
        _pathTracerPass = new PathTracerPass(downscaleFactor, maxBounces,useAccumulation, materialFuzz, materialIsMetal, materialColor, interpolationType);
        _pathTracerPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_pathTracerPass);
    }
}
