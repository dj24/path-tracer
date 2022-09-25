using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

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
    [Range(0.0f,1.0f)] public float blendAmount;
    public SamplesPerPixel samplesPerPixel;
    public bool useAccumulation;
    class PathTracerPass : ScriptableRenderPass
    {
        private RenderTargetIdentifier _colorBuffer;
        private const string ProfilerTag = "PathTracerPass";
        private readonly ComputeShader _computeShader;
        private readonly int _downscaleFactor;
        private readonly float _blendAmount;
        private float _verticalFov;
        private Vector3 _cameraDirection;
        private readonly int _samplesPerPixel;
        private readonly bool _useAccumulation;

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

        public PathTracerPass(int downscaleFactor, float blendAmount, int samplesPerPixel, bool useAccumulation)
        {
            _useAccumulation = useAccumulation;
            _blendAmount = blendAmount;
            _samplesPerPixel = samplesPerPixel;
            _downscaleFactor = downscaleFactor == 0 ? 1 : downscaleFactor;
            _computeShader = (ComputeShader)Resources.Load("Compute/PathTracer");
        }
        
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            _verticalFov = renderingData.cameraData.camera.fieldOfView;
            _cameraDirection = -renderingData.cameraData.camera.transform.forward;

            RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
            descriptor.depthBufferBits = 0;
            ConfigureInput(ScriptableRenderPassInput.Depth);
            _colorBuffer = renderingData.cameraData.renderer.cameraColorTarget;
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

            var mesh = meshFilters[0].sharedMesh;
            mesh.indexBufferTarget = GraphicsBuffer.Target.Raw;
            mesh.vertexBufferTarget = GraphicsBuffer.Target.Raw;
            var vertexBuffer = mesh.GetVertexBuffer(0);
            var indexBuffer = mesh.GetIndexBuffer();
            var triangleCount = (int)(meshFilters[0].sharedMesh.GetIndexCount(0) / 3);
            
            Debug.Log(indexBuffer.stride);
            Debug.Log(indexBuffer.count);

            // Init input and output texture
            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            var _sceneTexture = RenderTexture.GetTemporary(descriptor);
            var _upscaleTexture = RenderTexture.GetTemporary(descriptor);
            descriptor.enableRandomWrite = true;
            var _outputTexture = RenderTexture.GetTemporary(descriptor);
            descriptor.width /= _downscaleFactor;
            descriptor.height /= _downscaleFactor;
            var _downscaleTexture = RenderTexture.GetTemporary(descriptor);
            
            // Copy scene buffer into texture
            var cmd = CommandBufferPool.Get(ProfilerTag);
            cmd.Blit(_colorBuffer,_sceneTexture);
            
            // Execute compute
            _computeShader.SetBuffer(_kernelIndex,"VertexBuffer",vertexBuffer);
            _computeShader.SetBuffer(_kernelIndex,"IndexBuffer",indexBuffer);
            _computeShader.SetBool("UseAccumulation", _useAccumulation);
            _computeShader.SetInt("Width", _downscaleTexture.width);
            _computeShader.SetInt("TriangleCount", triangleCount);
            _computeShader.SetInt("Height", _downscaleTexture.height);
            _computeShader.SetInt("DownscaleFactor", _downscaleFactor);
            _computeShader.SetInt("SamplesPerPixel", _samplesPerPixel);
            _computeShader.SetFloat("BlendAmount", _blendAmount);
            _computeShader.SetFloat("VerticalFov", _verticalFov);
            _computeShader.SetVector("CameraDirection", new Vector4(_cameraDirection.x, _cameraDirection.y, _cameraDirection.z, 0));

            _computeShader.SetTexture(_kernelIndex, "Downscale", _downscaleTexture);
            _computeShader.SetTexture(_kernelIndex, "Skybox", reflectionProbe.texture);
            uint kernelX = 1;
            uint kernelY = 1;
            // _computeShader.GetKernelThreadGroupSizes(_kernelIndex, out kernelX,out kernelY, out _);
            int threadGroupsX = (int) (_downscaleTexture.width / kernelX); 
            int threadGroupsY = (int) (_downscaleTexture.height / kernelY);
            _computeShader.Dispatch(_kernelIndex, threadGroupsX, threadGroupsY, 1);
            
            var request = AsyncGPUReadback.Request(_outputTexture);
            request.WaitForCompletion();
            
            cmd.Blit(_downscaleTexture,_upscaleTexture);

            _computeShader.SetTexture((int)PathTracerKernel.Mix, "Result", _outputTexture);
            _computeShader.SetTexture((int)PathTracerKernel.Mix, "Upscale", _upscaleTexture);
            _computeShader.SetTexture((int)PathTracerKernel.Mix, "Scene", _sceneTexture);
            _computeShader.Dispatch((int)PathTracerKernel.Mix, _sceneTexture.width, _sceneTexture.height, 1);
            
            // Sync compute with frame
            request = AsyncGPUReadback.Request(_outputTexture);
            request.WaitForCompletion();
            
            // Clean up
            RenderTexture.ReleaseTemporary(_outputTexture);
            RenderTexture.ReleaseTemporary(_sceneTexture);
            RenderTexture.ReleaseTemporary(_upscaleTexture);
            RenderTexture.ReleaseTemporary(_downscaleTexture);
            vertexBuffer.Release();
            indexBuffer.Release();
            
            // Copy processed texture into scene buffer
            cmd.Blit(_outputTexture,_colorBuffer);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    public override void Create()
    {
        _pathTracerPass = new PathTracerPass(downscaleFactor, blendAmount, (int)samplesPerPixel, useAccumulation);
        _pathTracerPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_pathTracerPass);
    }
}
