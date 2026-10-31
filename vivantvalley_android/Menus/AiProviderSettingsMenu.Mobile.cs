using Microsoft.Xna.Framework;
using VivantValley.Services;

namespace VivantValley.Menus;

public sealed partial class AiProviderSettingsMenu
{
    private MobileFormPage? mobileForm;
    private void BuildMobileForm()
    {
        Rectangle safe = MobileRuntime.SafeBounds;
        xPositionOnScreen = safe.X; yPositionOnScreen = safe.Y; width = safe.Width; height = safe.Height;
        mobileForm = new MobileFormPage(this, L.T("text.26121bc0761a"), () => statusText);
        mobileForm.Text(() => L.T("mobile.config.local"));
        mobileForm.Button(() => "Vivant Valley", OpenHosted);
        mobileForm.Button(() => (activeProvider == AiProviderNames.DeepSeek ? "✓ " : "") + "DeepSeek", () => SwitchProvider(AiProviderNames.DeepSeek));
        mobileForm.Button(() => (activeProvider == AiProviderNames.OpenAI ? "✓ " : "") + "OpenAI (GPT)", () => SwitchProvider(AiProviderNames.OpenAI));
        mobileForm.Field(L.T("text.973b124d4b7b"), baseUrlBox);
        mobileForm.Field(L.T("text.c98e118e0a43"), modelBox);
        mobileForm.Field("API Key", apiKeyBox);
        mobileForm.Text(GetKeyLabel);
        mobileForm.Button(() => drafts[activeProvider].ClearSavedKey ? L.T("text.71eceebde9a4") : L.T("text.50d36f0db2da"), () =>
        {
            DraftState state = drafts[activeProvider];
            state.ClearSavedKey = !state.ClearSavedKey;
            state.ReplacementKey = apiKeyBox.Text = string.Empty;
        });
        mobileForm.Button(() => L.T("text.87a81194f818") + $"  −  ({conversationUiScale:P0})", () => AdjustConversationUiScale(-.1f));
        mobileForm.Button(() => L.T("text.87a81194f818") + "  +", () => AdjustConversationUiScale(.1f));
        mobileForm.Button(() => L.T("text.a5e8416e67be") + $"  −  ({proactiveUiScale:P0})", () => AdjustProactiveUiScale(-.1f));
        mobileForm.Button(() => L.T("text.a5e8416e67be") + "  +", () => AdjustProactiveUiScale(.1f));
        mobileForm.Action(() => pendingTest is null ? L.T("text.0a98a48eaaac") : L.T("text.105a9082c346"), StartTest, () => pendingTest is null);
        mobileForm.Action(() => L.T("text.a3030bf8f16d"), Save, () => pendingTest is null);
        mobileForm.AddHudSettings();
        mobileForm.Action(() => L.T("mobile.setup.later"), Cancel);
        mobileForm.Reflow();
    }
    protected override void OnDrag(Vector2 start, float amount)
    { if (MobileRuntime.Enabled) mobileForm?.Scroll(start, amount); else base.OnDrag(start, amount); }
    public override void receiveScrollWheelAction(int direction)
    { if (MobileRuntime.Enabled) mobileForm?.Wheel(direction); else base.receiveScrollWheelAction(direction); }
}
