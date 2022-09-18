using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PathTracer : ScriptableRendererFeature
{
    private PathTracerPass _pathTracerPass;
    [Range(1,8)] public int downscaleFactor = 1;
    [Range(0.0f,1.0f)] public float blendAmount;
    class PathTracerPass : ScriptableRenderPass
    {
        private RenderTargetIdentifier _colorBuffer;
        private const string ProfilerTag = "PathTracerPass";
        private readonly ComputeShader _computeShader;
        private readonly int _downscaleFactor;
        private readonly float _blendAmount;
        public PathTracerPass(int downscaleFactor, float blendAmount)
        {
            _blendAmount = blendAmount;
            _downscaleFactor = downscaleFactor == 0 ? 1 : downscaleFactor;
            _computeShader = (ComputeShader)Resources.Load("Compute/PathTracer");
        }
        
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
            descriptor.depthBufferBits = 0;
            ConfigureInput(ScriptableRenderPassInput.Depth);
            _colorBuffer = renderingData.cameraData.renderer.cameraColorTarget;
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // Init input and output texture
            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            var _sceneTexture = RenderTexture.GetTemporary(descriptor);
            descriptor.enableRandomWrite = true;
            var _outputTexture = RenderTexture.GetTemporary(descriptor);
            descriptor.width /= _downscaleFactor;
            descriptor.height /= _downscaleFactor;
            var _downscaleTexture = RenderTexture.GetTemporary(descriptor);
            
            // Copy scene buffer into texture
            var cmd = CommandBufferPool.Get(ProfilerTag);
            cmd.Blit(_colorBuffer,_sceneTexture);
            
            // Execute compute
            _computeShader.SetTexture(0, "Result", _outputTexture);
            _computeShader.SetTexture(0, "Downscale", _downscaleTexture);
            _computeShader.SetTexture(0, "Scene", _sceneTexture);
            _computeShader.SetTexture(1, "Result", _outputTexture);
            _computeShader.SetTexture(1, "Downscale", _downscaleTexture);
            _computeShader.SetTexture(1, "Scene", _sceneTexture);
            _computeShader.SetInt("Width", _downscaleTexture.width);
            _computeShader.SetInt("Height", _downscaleTexture.height);
            _computeShader.SetInt("DownscaleFactor", _downscaleFactor);
            _computeShader.SetFloat("BlendAmount", _blendAmount);
            _computeShader.Dispatch(0, _downscaleTexture.width, _downscaleTexture.height, 1);
            _computeShader.Dispatch(1, _sceneTexture.width, _sceneTexture.height, 1);
            
            // Sync compute with frame
            var request = AsyncGPUReadback.Request(_outputTexture);
            request.WaitForCompletion();
            
            // Clean up
            RenderTexture.ReleaseTemporary(_outputTexture);
            RenderTexture.ReleaseTemporary(_sceneTexture);
            RenderTexture.ReleaseTemporary(_downscaleTexture);
            
            // Copy processed texture into scene buffer
            cmd.Blit(_outputTexture,_colorBuffer);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    public override void Create()
    {
        _pathTracerPass = new PathTracerPass(downscaleFactor, blendAmount);
        _pathTracerPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_pathTracerPass);
    }
}
