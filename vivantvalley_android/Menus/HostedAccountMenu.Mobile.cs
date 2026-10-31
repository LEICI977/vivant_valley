using Microsoft.Xna.Framework;
using VivantValley.Services;

namespace VivantValley.Menus;

public sealed partial class HostedAccountMenu
{
    private MobileFormPage? mobileForm;
    private void BuildMobileForm()
    {
        Rectangle safe = MobileRuntime.SafeBounds;
        xPositionOnScreen = safe.X; yPositionOnScreen = safe.Y; width = safe.Width; height = safe.Height;
        mobileForm = new MobileFormPage(this, L.T("text.944d9a39d6a2"), () => status);
        mobileForm.Text(() => mode == AuthMode.PasswordReset ? L.T("text.0c95fcddd26f") : priceSummary?.Invoke() ?? L.T("speech.loadingPrices"));
        mobileForm.Field(L.T("text.73075237fd0f"), emailBox, email: true);
        mobileForm.Field(L.T(mode == AuthMode.PasswordReset ? "text.566f6ab7157d" : "text.17fae5507a3f"), passwordBox);
        if (mode != AuthMode.Login)
        {
            mobileForm.Field(L.T("text.22baa091f796"), verificationBox);
            mobileForm.Button(VerificationButtonText, () => StartVerificationCode(mode == AuthMode.PasswordReset),
                () => pendingVerification is null && pendingReset is null && CanRequestVerificationCode());
        }
        if (mode == AuthMode.PasswordReset)
        {
            mobileForm.Field(L.T("text.6fde05a916c6"), confirmPasswordBox);
            mobileForm.Action(() => L.T("text.d70928debc2e"), StartPasswordReset, () => pendingReset is null && challengeId.Length > 0);
            mobileForm.Action(() => L.T("text.747b0f9082e6"), ShowLogin, () => pendingReset is null);
        }
        else
        {
            mobileForm.Field(L.T("text.edd17db6283f"), modelBox);
            if (mode == AuthMode.Login)
            {
                mobileForm.Field(L.T("text.a8acfd2e41c3"), redeemBox);
                mobileForm.Button(() => L.T("text.6b8e251935fd"), StartRedeem, () => pendingRedeem is null && authToken.Length > 0);
                mobileForm.Text(FormatBalance);
                mobileForm.Text(() => L.T("text.523b7f1d570c"));
                foreach (int denomination in purchaseButtons.Keys)
                {
                    int amount = denomination;
                    mobileForm.Button(() => L.F("text.c93f57f25d7e", $"购买 ¥{HostedPurchaseCatalog.PurchaseDisplayAmount(amount, L.Locale)}"), () => OpenPurchasePage(amount));
                }
                AddMobileSpeechControls();
                mobileForm.Button(() => L.T("text.c4fb62202bad"), ShowRegister, () => pending is null);
                mobileForm.Button(() => L.T("text.d832a97894fc"), ShowPasswordReset, () => pending is null);
                mobileForm.Action(() => L.T("text.1e2df9c3075a"), () => Start(false), () => pending is null);
            }
            else
            {
                AddMobileSpeechControls();
                mobileForm.Button(() => L.T("text.747b0f9082e6"), ShowLogin, () => pending is null);
                mobileForm.Action(() => L.T("text.e1ec379a4043"), () => Start(true), () => pending is null && challengeId.Length > 0);
            }
        }
        mobileForm.Action(() => "DeepSeek / OpenAI", OpenDirectSettings);
        if (confirmMobileConfiguration is not null)
            mobileForm.Button(() => L.T("mobile.config.confirm"), () =>
            {
                AiSettingsSaveResult result = confirmMobileConfiguration();
                status = result.Message;
                if (result.Success) Close();
            });
        mobileForm.Text(() => L.T("mobile.config.local"));
        mobileForm.AddHudSettings();
        mobileForm.Action(() => L.T("mobile.setup.later"), Close);
        mobileForm.Reflow();
    }
    private void AddMobileSpeechControls()
    {
        if (mobileForm is null) return;
        if (speechEnabled is not null && toggleSpeech is not null && onOpenSpeech is not null)
            mobileForm.ButtonPair(SpeechToggleLabel, ToggleSpeech, () => CanToggleSpeech, SpeechToggleColor,
                () => L.T("speech.title"), OpenSpeech, () => pending is null && pendingReset is null);
        else if (onOpenSpeech is not null)
            mobileForm.Button(() => L.T("speech.title"), OpenSpeech, () => pending is null && pendingReset is null);
    }
    protected override void OnDrag(Vector2 start, float amount)
    { if (MobileRuntime.Enabled) mobileForm?.Scroll(start, amount); else base.OnDrag(start, amount); }
    public override void receiveScrollWheelAction(int direction)
    { if (MobileRuntime.Enabled) mobileForm?.Wheel(direction); else base.receiveScrollWheelAction(direction); }
}
