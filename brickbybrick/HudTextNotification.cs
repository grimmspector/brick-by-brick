using System;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace brickbybrick
{
    // Renders one replaceable, transient player notification just below the crosshair.
    internal sealed class HudTextNotification : IRenderer
    {
        private const float ColorTransitionSeconds = 1f;
        private const float HoldSeconds = 3f;
        private const float FadeOutSeconds = 0.75f;
        private const float CrosshairOffsetPixels = 28f;
        private const int TextureWidth = 720;
        private const int TextureHeight = 64;

        private readonly ICoreClientAPI api;
        private LoadedTexture? textTexture;
        private float elapsedSeconds;

        public HudTextNotification(ICoreClientAPI api)
        {
            this.api = api;
            api.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "brickbybrick-hud-text");
        }

        public double RenderOrder => 1.01;

        public int RenderRange => 0;

        public void Show(string message)
        {
            textTexture?.Dispose();
            textTexture = api.Gui.TextTexture.GenTextTexture(
                message,
                CairoFont.WhiteMediumText().WithFontSize(17).WithStroke(new[] { 0d, 0d, 0d, 0.65d }, 1.25),
                TextureWidth,
                TextureHeight,
                null,
                EnumTextOrientation.Center,
                false);
            elapsedSeconds = 0f;
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (textTexture == null || api.HideGuis) return;

            elapsedSeconds += deltaTime;
            float fadeStart = ColorTransitionSeconds + HoldSeconds;
            float lifetime = fadeStart + FadeOutSeconds;
            if (elapsedSeconds >= lifetime)
            {
                textTexture.Dispose();
                textTexture = null;
                return;
            }

            float colorProgress = Math.Min(elapsedSeconds / ColorTransitionSeconds, 1f);
            float alpha = elapsedSeconds <= fadeStart
                ? 1f
                : 1f - ((elapsedSeconds - fadeStart) / FadeOutSeconds);
            Vec4f color = new(1f * alpha, colorProgress * alpha, colorProgress * alpha, alpha);
            float x = (api.Render.FrameWidth - textTexture.Width) / 2f;
            float y = api.Render.FrameHeight / 2f + CrosshairOffsetPixels;
            api.Render.Render2DTexturePremultipliedAlpha(
                textTexture.TextureId,
                x,
                y,
                textTexture.Width,
                textTexture.Height,
                50,
                color);
        }

        public void Dispose()
        {
            textTexture?.Dispose();
            textTexture = null;
            api.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        }
    }
}
