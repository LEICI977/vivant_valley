using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Services;

namespace VivantValley.Menus;

/// <summary>In-game account login/register for the Vivant Valley hosted service.</summary>
public sealed class HostedAccountMenu : IClickableMenu
{
    private enum AuthMode
    {
        Login,
        Register,
        PasswordReset,
    }

    private readonly Func<string, string, bool, string, string, Task<(bool Success, string Message, string Token, string Model, long BalanceMicros)>> authenticate;
    private readonly Func<string, bool, Task<(bool Success, string Message, string ChallengeId)>>? requestVerificationCode;
    private readonly Func<string, string, string, string, Task<(bool Success, string Message)>>? resetPassword;
    private readonly Action<string, string, long> onSuccess;
    private readonly Func<string, string, Task<string>>? redeem;
    private readonly Func<string, Task<long>>? refreshBalance;
    private readonly Action? onOpenDirectSettings;
    private readonly Action? onOpenSpeech;
    private readonly Func<string>? priceSummary;
    private readonly Func<bool>? speechEnabled;
    private readonly Action? toggleSpeech;
    private readonly ClickableComponent speechToggleButton = new(Rectangle.Empty, "speech-toggle");
    private bool CompactAccountTools => height < 660;
    private bool CanToggleSpeech => speechEnabled is not null && toggleSpeech is not null && pending is null && pendingReset is null;
    private string SpeechToggleLabel() => L.T(speechEnabled?.Invoke() == true ? "speech.enabled" : "speech.disabled");
    private Color SpeechToggleColor() => speechEnabled?.Invoke() == true ? Color.DarkGreen : Color.DarkRed;
    private void ToggleSpeech() { if (CanToggleSpeech) toggleSpeech!(); }
    private readonly ClickableComponent speechSettingsButton = new(Rectangle.Empty, "speech-settings");
    private readonly TextBox emailBox;
    private readonly TextBox passwordBox;
    private readonly TextBox verificationBox;
    private readonly TextBox confirmPasswordBox;
    private readonly TextBox modelBox;
    private readonly CaretTextBox redeemBox;
    private readonly ClickableComponent loginButton = new(Rectangle.Empty, "login");
    private readonly ClickableComponent registerButton = new(Rectangle.Empty, "register");
    private readonly ClickableComponent sendCodeButton = new(Rectangle.Empty, "send-code");
    private readonly ClickableComponent forgotPasswordButton = new(Rectangle.Empty, "forgot-password");
    private readonly ClickableComponent resetPasswordButton = new(Rectangle.Empty, "reset-password");
    private readonly ClickableComponent backButton = new(Rectangle.Empty, "back");
    private readonly ClickableComponent directSettingsButton = new(Rectangle.Empty, "direct-settings");
    private readonly ClickableComponent redeemButton = new(Rectangle.Empty, "redeem");
    private readonly Dictionary<int, ClickableComponent> purchaseButtons = new();
    private string purchaseLocale = string.Empty;
    private void RefreshPurchaseOptions()
    {
        if (purchaseLocale == L.Locale) return;
        purchaseLocale = L.Locale;
        purchaseButtons.Clear();
        foreach (HostedPurchaseOption option in HostedPurchaseCatalog.ForLocale(purchaseLocale))
            purchaseButtons.Add(option.Amount, new ClickableComponent(Rectangle.Empty, "purchase-" + option.Amount));
    }
    private Task<(bool Success, string Message, string Token, string Model, long BalanceMicros)>? pending;
    private Task<(bool Success, string Message, string ChallengeId)>? pendingVerification;
    private Task<(bool Success, string Message)>? pendingReset;
    private Task<string>? pendingRedeem;
    private Task<long>? pendingBalance;
    private string authToken;
    private long balanceMicros = -1;
    private bool closed;
    private AuthMode mode = AuthMode.Login;
    private string challengeId = string.Empty;
    private DateTime registrationVerificationAvailableAtUtc;
    private DateTime passwordResetVerificationAvailableAtUtc;
    private AuthMode? pendingVerificationMode;
    private string pendingVerificationEmail = string.Empty;
    private string challengeEmail = string.Empty;
    private string status = L.T("text.b458b1aab0a0");
    private Color statusColor = Game1.textColor;

    public HostedAccountMenu(
        Func<string, string, bool, string, string, Task<(bool Success, string Message, string Token, string Model, long BalanceMicros)>> authenticate,
        Action<string, string, long> onSuccess,
        Func<string, string, Task<string>>? redeem = null,
        Action? onCancel = null,
        Action? onOpenDirectSettings = null,
        Func<string, Task<long>>? refreshBalance = null,
        Func<string, bool, Task<(bool Success, string Message, string ChallengeId)>>? requestVerificationCode = null,
        Func<string, string, string, string, Task<(bool Success, string Message)>>? resetPassword = null,
        string? initialToken = null,
        string? initialModel = null,
        Action? onOpenSpeech = null,
        Func<string>? priceSummary = null,
        Func<bool>? speechEnabled = null,
        Action? toggleSpeech = null)
    {
        this.authenticate = authenticate ?? throw new ArgumentNullException(nameof(authenticate));
        this.onSuccess = onSuccess ?? throw new ArgumentNullException(nameof(onSuccess));
        this.redeem = redeem;
        this.refreshBalance = refreshBalance;
        this.onOpenDirectSettings = onOpenDirectSettings;
        this.onOpenSpeech = onOpenSpeech;
        this.priceSummary = priceSummary;
        this.speechEnabled = speechEnabled;
        this.toggleSpeech = toggleSpeech;
        this.requestVerificationCode = requestVerificationCode;
        this.resetPassword = resetPassword;
        authToken = initialToken?.Trim() ?? string.Empty;
        Texture2D texture = Game1.content.Load<Texture2D>("LooseSprites\\textBox");
        emailBox = CreateTextBox(texture, 320, false);
        passwordBox = CreateTextBox(texture, 320, true);
        verificationBox = CreateTextBox(texture, 6, false);
        confirmPasswordBox = CreateTextBox(texture, 320, true);
        modelBox = CreateTextBox(texture, 80, false);
        modelBox.Text = string.IsNullOrWhiteSpace(initialModel) ? "vv-dialogue" : initialModel.Trim();
        redeemBox = new CaretTextBox(texture, Game1.smallFont, Game1.textColor, textLimit: 128);
        width = 760;
        height = 700;
        Reposition();
        initializeUpperRightCloseButton();
        Select(emailBox);
        if (authToken.Length > 0 && refreshBalance is not null)
            pendingBalance = refreshBalance(authToken);
    }

    public override void update(GameTime time)
    {
        base.update(time);
        if (purchaseLocale != L.Locale) Reposition();
        if (pending is not null && pending.IsCompleted)
        {
            var task = pending; pending = null;
            try
            {
                var result = task.GetAwaiter().GetResult();
                if (result.Success)
                {
                    authToken = result.Token;
                    balanceMicros = result.BalanceMicros;
                    onSuccess(result.Token, string.IsNullOrWhiteSpace(modelBox.Text) ? result.Model : modelBox.Text.Trim(), balanceMicros);
                    status = L.T(balanceMicros < 0 ? "hosted.signedInBalancePending" : "text.0a4b73924caa");
                    statusColor = Color.DarkGreen;
                }
                else { status = result.Message; statusColor = Color.DarkRed; }
            }
            catch (Exception ex) { status = ex.Message; statusColor = Color.DarkRed; }
        }

        if (pendingVerification is not null && pendingVerification.IsCompleted)
        {
            Task<(bool Success, string Message, string ChallengeId)> task = pendingVerification;
            pendingVerification = null;
            AuthMode? requestedMode = pendingVerificationMode;
            pendingVerificationMode = null;
            string requestedEmail = pendingVerificationEmail;
            pendingVerificationEmail = string.Empty;
            if (requestedMode is null
                || requestedMode.Value != mode
                || !string.Equals(requestedEmail, (emailBox.Text ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                try { _ = task.GetAwaiter().GetResult(); }
                catch { }
            }
            else
            {
                try
                {
                    var result = task.GetAwaiter().GetResult();
                    if (result.Success)
                    {
                        challengeId = result.ChallengeId;
                        challengeEmail = requestedEmail;
                        SetVerificationAvailableAtUtc(mode, DateTime.UtcNow.AddSeconds(60));
                        status = mode == AuthMode.Register
                            ? L.T("text.ed9698985017")
                            : L.T("text.99352232e5fd");
                        statusColor = Color.DarkGreen;
                    }
                    else
                    {
                        status = result.Message;
                        statusColor = Color.DarkRed;
                    }
                }
                catch (Exception ex) { status = ex.Message; statusColor = Color.DarkRed; }
            }
        }

        if (pendingReset is not null && pendingReset.IsCompleted)
        {
            Task<(bool Success, string Message)> task = pendingReset;
            pendingReset = null;
            try
            {
                var result = task.GetAwaiter().GetResult();
                status = result.Message;
                statusColor = result.Success ? Color.DarkGreen : Color.DarkRed;
                if (result.Success)
                {
                    emailBox.Text = string.Empty;
                    passwordBox.Text = string.Empty;
                    verificationBox.Text = string.Empty;
                    confirmPasswordBox.Text = string.Empty;
                    authToken = string.Empty;
                    balanceMicros = -1;
                    challengeId = string.Empty;
                    challengeEmail = string.Empty;
                    mode = AuthMode.Login;
                    Reposition();
                    Select(emailBox);
                }
            }
            catch (Exception ex) { status = ex.Message; statusColor = Color.DarkRed; }
        }

        if (pendingRedeem is not null && pendingRedeem.IsCompleted)
        {
            try { status = pendingRedeem.GetAwaiter().GetResult(); statusColor = Color.DarkGreen; }
            catch (Exception ex) { status = ex.Message; statusColor = Color.DarkRed; }
            pendingRedeem = null;
            StartBalanceRefresh();
        }

        if (pendingBalance is not null && pendingBalance.IsCompleted)
        {
            Task<long> task = pendingBalance;
            pendingBalance = null;
            try
            {
                balanceMicros = task.GetAwaiter().GetResult();
            }
            catch
            {
                status = L.T("text.3ac658114ad2");
                statusColor = Color.DarkRed;
            }
        }
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        Reposition();
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (upperRightCloseButton?.containsPoint(x, y) == true) { Close(); return; }
        if (mode == AuthMode.Login && redeemBox.Bounds.Contains(x, y))
        {
            Select(redeemBox, x);
            return;
        }
        if (mode == AuthMode.PasswordReset)
        {
            if (sendCodeButton.containsPoint(x, y)) { StartVerificationCode(reset: true); return; }
            if (resetPasswordButton.containsPoint(x, y)) { StartPasswordReset(); return; }
            if (backButton.containsPoint(x, y) && pendingReset is null) { ShowLogin(); return; }
        }
        else
        {
            if (loginButton.containsPoint(x, y))
            {
                if (pending is not null) return;
                if (mode == AuthMode.Register) ShowLogin();
                else Start(false);
                return;
            }
            if (registerButton.containsPoint(x, y))
            {
                if (pending is not null) return;
                if (mode == AuthMode.Login) ShowRegister();
                else Start(true);
                return;
            }
            if (mode == AuthMode.Register && sendCodeButton.containsPoint(x, y)) { StartVerificationCode(reset: false); return; }
            if (forgotPasswordButton.containsPoint(x, y) && pending is null) { ShowPasswordReset(); return; }
        }
        if (speechToggleButton.containsPoint(x, y)) { ToggleSpeech(); return; }
        if (speechSettingsButton.containsPoint(x, y) && onOpenSpeech is not null) { OpenSpeech(); return; }
        if (directSettingsButton.containsPoint(x, y)) { OpenDirectSettings(); return; }
        if (mode == AuthMode.Login && redeemButton.containsPoint(x, y) && authToken.Length > 0) { StartRedeem(); return; }
        foreach ((int denomination, ClickableComponent button) in purchaseButtons)
        {
            if (mode == AuthMode.Login && button.containsPoint(x, y))
            {
                OpenPurchasePage(denomination);
                return;
            }
        }
        if (Contains(emailBox, x, y)) Select(emailBox);
        else if (Contains(passwordBox, x, y)) Select(passwordBox);
        else if (Contains(verificationBox, x, y)) Select(verificationBox);
        else if (Contains(confirmPasswordBox, x, y)) Select(confirmPasswordBox);
        else if (Contains(modelBox, x, y)) Select(modelBox);
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape) { Close(); return; }
        if (key == Keys.Tab)
        {
            SelectNextField();
            return;
        }
        if (!emailBox.Selected && !passwordBox.Selected && !verificationBox.Selected
            && !confirmPasswordBox.Selected && !modelBox.Selected && !redeemBox.Selected)
            base.receiveKeyPress(key);
    }

    public override void performHoverAction(int x, int y)
    {
        emailBox.Hover(x, y); passwordBox.Hover(x, y); modelBox.Hover(x, y); redeemBox.Hover(x, y);
        verificationBox.Hover(x, y); confirmPasswordBox.Hover(x, y);
        loginButton.scale = loginButton.containsPoint(x, y) ? 1.03f : 1f;
        registerButton.scale = registerButton.containsPoint(x, y) ? 1.03f : 1f;
        sendCodeButton.scale = sendCodeButton.containsPoint(x, y) ? 1.03f : 1f;
        forgotPasswordButton.scale = forgotPasswordButton.containsPoint(x, y) ? 1.03f : 1f;
        resetPasswordButton.scale = resetPasswordButton.containsPoint(x, y) ? 1.03f : 1f;
        backButton.scale = backButton.containsPoint(x, y) ? 1.03f : 1f;
        directSettingsButton.scale = directSettingsButton.containsPoint(x, y) ? 1.03f : 1f;
        redeemButton.scale = redeemButton.containsPoint(x, y) ? 1.03f : 1f;
        foreach (ClickableComponent button in purchaseButtons.Values)
            button.scale = button.containsPoint(x, y) ? 1.03f : 1f;
        upperRightCloseButton?.tryHover(x, y, 0.2f);
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.45f);
        drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);
        b.DrawString(Game1.dialogueFont, NativeDialogueFrame.FitSingleLine(L.T("text.944d9a39d6a2"), Game1.dialogueFont, width - 100), new Vector2(xPositionOnScreen + 36, yPositionOnScreen + 24), Game1.textColor);
        if (mode != AuthMode.PasswordReset)
        {
            if (speechEnabled is not null && toggleSpeech is not null)
                DrawButton(b, speechToggleButton, SpeechToggleLabel(), !CanToggleSpeech, SpeechToggleColor());
            if (onOpenSpeech is not null) DrawButton(b, speechSettingsButton, L.T("speech.title"), pending is not null);
        }
        if (mode == AuthMode.PasswordReset)
        {
            b.DrawString(Game1.smallFont, L.T("text.0c95fcddd26f"), new Vector2(xPositionOnScreen + 36, yPositionOnScreen + 58), Game1.textColor);
            DrawAuthFields(b, includePassword: true, includeVerification: true, includeConfirmPassword: true);
            DrawButton(b, sendCodeButton, VerificationButtonText(), pendingVerification is not null || pendingReset is not null || !CanRequestVerificationCode());
            DrawButton(b, resetPasswordButton, L.T("text.d70928debc2e"), pendingReset is not null || challengeId.Length == 0);
            DrawButton(b, backButton, L.T("text.747b0f9082e6"), pendingReset is not null);
            b.DrawString(Game1.smallFont, status, new Vector2(xPositionOnScreen + 36, backButton.bounds.Y - 30), statusColor);
        }
        else
        {
            b.DrawString(Game1.smallFont, NativeDialogueFrame.FitSingleLine(priceSummary?.Invoke() ?? L.T("speech.loadingPrices"),Game1.smallFont,width-72), new Vector2(xPositionOnScreen + 36, yPositionOnScreen + 58), Game1.textColor);
            DrawAuthFields(b, includePassword: true, includeVerification: mode == AuthMode.Register, includeConfirmPassword: false);
            if (mode == AuthMode.Register)
                DrawButton(b, sendCodeButton, VerificationButtonText(), pendingVerification is not null || pending is not null || !CanRequestVerificationCode());
            if (mode == AuthMode.Login)
                DrawAccountTools(b);
            DrawButton(b, loginButton, mode == AuthMode.Register ? L.T("text.747b0f9082e6") : L.T("text.1e2df9c3075a"), pending is not null);
            DrawButton(b, registerButton, mode == AuthMode.Register ? L.T("text.e1ec379a4043") : L.T("text.c4fb62202bad"), pending is not null || mode == AuthMode.Register && challengeId.Length == 0);
            DrawButton(b, forgotPasswordButton, L.T("text.d832a97894fc"), pending is not null);
            DrawButton(b, directSettingsButton, "DeepSeek / OpenAI", false);
            b.DrawString(Game1.smallFont, status, new Vector2(xPositionOnScreen + 36, directSettingsButton.bounds.Y - 30), statusColor);
        }
        upperRightCloseButton?.draw(b); drawMouse(b);
    }

    private void Start(bool register)
    {
        if (pending is not null || register && mode != AuthMode.Register) return;
        string email = (emailBox.Text ?? string.Empty).Trim(); string password = passwordBox.Text ?? string.Empty;
        if (email.Length == 0 || password.Length < 12) { status = L.T("text.61e4b12e98b6"); statusColor = Color.DarkRed; return; }
        if (register && (challengeId.Length == 0
                         || !string.Equals(email, challengeEmail, StringComparison.OrdinalIgnoreCase)
                         || verificationBox.Text.Trim().Length != 6))
        {
            status = L.T("text.91d634ed63cf");
            statusColor = Color.DarkRed;
            return;
        }
        status = register ? L.T("text.4429b8ba3956") : L.T("text.d8712b0d82e6"); statusColor = Color.Gray;
        pending = authenticate(email, password, register, challengeId, verificationBox.Text.Trim());
    }

    private void StartVerificationCode(bool reset)
    {
        if (pendingVerification is not null || pending is not null || pendingReset is not null || requestVerificationCode is null
            || reset != (mode == AuthMode.PasswordReset) || mode == AuthMode.Login
            || !CanRequestVerificationCode())
            return;
        string email = (emailBox.Text ?? string.Empty).Trim();
        if (email.Length == 0)
        {
            status = L.T("text.05328417bd39");
            statusColor = Color.DarkRed;
            return;
        }
        challengeId = string.Empty;
        challengeEmail = string.Empty;
        pendingVerificationMode = mode;
        pendingVerificationEmail = email;
        status = reset ? L.T("text.b4c70c5479a7") : L.T("text.37ddd22a05f5");
        statusColor = Color.Gray;
        pendingVerification = requestVerificationCode(email, reset);
    }

    private void StartPasswordReset()
    {
        if (pendingReset is not null || resetPassword is null)
            return;
        string email = (emailBox.Text ?? string.Empty).Trim();
        string otp = (verificationBox.Text ?? string.Empty).Trim();
        string password = passwordBox.Text ?? string.Empty;
        string confirmation = confirmPasswordBox.Text ?? string.Empty;
        if (email.Length == 0
            || challengeId.Length == 0
            || !string.Equals(email, challengeEmail, StringComparison.OrdinalIgnoreCase)
            || otp.Length != 6
            || password.Length < 12)
        {
            status = L.T("text.d41af2f417ae");
            statusColor = Color.DarkRed;
            return;
        }
        if (!password.Equals(confirmation, StringComparison.Ordinal))
        {
            status = L.T("text.a2ba2545a60e");
            statusColor = Color.DarkRed;
            return;
        }
        status = L.T("text.bd2dacd91bec");
        statusColor = Color.Gray;
        pendingReset = resetPassword(email, challengeId, otp, password);
    }

    private void StartRedeem()
    {
        if (pendingRedeem is not null || redeem is null) return;
        string code = (redeemBox.Text ?? string.Empty).Trim();
        if (code.Length == 0) { status = L.T("text.4a295dcdba3f"); statusColor = Color.DarkRed; return; }
        status = L.T("text.3e216bcb6b32"); statusColor = Color.Gray;
        pendingRedeem = redeem(authToken, code);
    }

    private void StartBalanceRefresh()
    {
        if (authToken.Length == 0 || refreshBalance is null || pendingBalance is not null)
            return;
        try { pendingBalance = refreshBalance(authToken); }
        catch { status = L.T("text.3ac658114ad2"); statusColor = Color.DarkRed; }
    }

    private string FormatBalance()
    {
        return balanceMicros < 0 && authToken.Length > 0
            ? L.T("hosted.balanceUnavailable") : HostedPurchaseCatalog.Balance(balanceMicros);
    }

    private void OpenPurchasePage(int denomination)
    {
        HostedPurchaseOption? option = HostedPurchaseCatalog.ForLocale(L.Locale).FirstOrDefault(item => item.Amount == denomination);
        if (option is null) return;
        string url = option.Url;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            status = L.F("text.9d0061822340", $"已打开 ¥{HostedPurchaseCatalog.PurchaseDisplayAmount(denomination, L.Locale)} 购买页面，购买后请将卡密粘贴到兑换框。");
            statusColor = Color.DarkGreen;
        }
        catch
        {
            status = L.F("text.285f3ea4a64b", $"无法自动打开购买页面，请手动访问：{url}");
            statusColor = Color.DarkRed;
        }
    }

    private void Reposition()
    {
        RefreshPurchaseOptions();
        width = Math.Min(900, Math.Max(680, Game1.uiViewport.Width - 48));
        height = Math.Min(700, Math.Max(600, Game1.uiViewport.Height - 48));
        xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
        yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

        speechToggleButton.bounds = speechSettingsButton.bounds = Rectangle.Empty;
        int fieldX = xPositionOnScreen + 36;
        int fieldWidth = width - 72;
        int firstFieldY = yPositionOnScreen + 118;
        emailBox.X = fieldX; emailBox.Y = firstFieldY; emailBox.Width = fieldWidth; emailBox.Height = 44;
        passwordBox.X = fieldX; passwordBox.Y = emailBox.Y + emailBox.Height + 22; passwordBox.Width = fieldWidth; passwordBox.Height = 44;
        verificationBox.X = fieldX; verificationBox.Y = passwordBox.Y + passwordBox.Height + 22;
        verificationBox.Height = 44;
        confirmPasswordBox.X = fieldX; confirmPasswordBox.Y = verificationBox.Y + verificationBox.Height + 22;
        confirmPasswordBox.Width = fieldWidth; confirmPasswordBox.Height = 44;
        modelBox.X = fieldX; modelBox.Width = fieldWidth; modelBox.Height = 44;

        if (mode == AuthMode.PasswordReset)
        {
            verificationBox.Width = Math.Max(300, fieldWidth - 232);
            sendCodeButton.bounds = new Rectangle(
                verificationBox.X + verificationBox.Width + 12,
                verificationBox.Y,
                fieldWidth - verificationBox.Width - 12,
                44);
            resetPasswordButton.bounds = new Rectangle(fieldX, yPositionOnScreen + height - 68, Math.Min(190, fieldWidth / 3), 48);
            backButton.bounds = new Rectangle(resetPasswordButton.bounds.Right + 14, resetPasswordButton.bounds.Y, Math.Min(190, fieldWidth / 3), 48);
            directSettingsButton.bounds = new Rectangle(
                backButton.bounds.Right + 14,
                backButton.bounds.Y,
                fieldWidth - resetPasswordButton.bounds.Width - backButton.bounds.Width - 28,
                48);
            initializeUpperRightCloseButton();
            return;
        }

        if (mode == AuthMode.Register)
        {
            verificationBox.Width = Math.Max(300, fieldWidth - 232);
            sendCodeButton.bounds = new Rectangle(
                verificationBox.X + verificationBox.Width + 12,
                verificationBox.Y,
                fieldWidth - verificationBox.Width - 12,
                44);
            modelBox.Y = verificationBox.Y + verificationBox.Height + 22;
        }
        else
        {
            modelBox.Y = passwordBox.Y + passwordBox.Height + 22;
            verificationBox.Width = fieldWidth;
            sendCodeButton.bounds = Rectangle.Empty;
        }

        redeemBox.X = fieldX; redeemBox.Y = modelBox.Y + modelBox.Height + 22; redeemBox.Width = Math.Max(360, fieldWidth - 158); redeemBox.Height = 44;
        redeemButton.bounds = new Rectangle(redeemBox.X + redeemBox.Width + 12, redeemBox.Y, fieldWidth - redeemBox.Width - 12, 44);

        int toolButtonHeight = CompactAccountTools ? 40 : 44;
        int purchaseY = redeemBox.Y + redeemBox.Height + (CompactAccountTools ? 44 : 88);
        const int purchaseGap = 8;
        int purchaseWidth = (fieldWidth - purchaseGap * (purchaseButtons.Count - 1)) / purchaseButtons.Count;
        int index = 0;
        foreach (ClickableComponent button in purchaseButtons.Values)
        {
            button.bounds = new Rectangle(fieldX + index * (purchaseWidth + purchaseGap), purchaseY, purchaseWidth, toolButtonHeight);
            index++;
        }

        int speechY = mode == AuthMode.Login ? purchaseY + toolButtonHeight + (CompactAccountTools ? 8 : 12) : modelBox.Y + modelBox.Height + 20;
        int speechWidth = (fieldWidth - 14) / 2;
        speechToggleButton.bounds = new Rectangle(fieldX, speechY, speechWidth, toolButtonHeight);
        speechSettingsButton.bounds = new Rectangle(fieldX + speechWidth + 14, speechY, fieldWidth - speechWidth - 14, toolButtonHeight);

        int buttonY = yPositionOnScreen + height - 68;
        int actionWidth = Math.Min(180, (fieldWidth - 42) / 4);
        loginButton.bounds = new Rectangle(fieldX, buttonY, actionWidth, 48);
        registerButton.bounds = new Rectangle(loginButton.bounds.Right + 14, buttonY, actionWidth, 48);
        forgotPasswordButton.bounds = new Rectangle(registerButton.bounds.Right + 14, buttonY, actionWidth, 48);
        directSettingsButton.bounds = new Rectangle(forgotPasswordButton.bounds.Right + 14, buttonY, fieldWidth - actionWidth * 3 - 42, 48);
        initializeUpperRightCloseButton();
    }

    private void DrawAuthFields(SpriteBatch b, bool includePassword, bool includeVerification, bool includeConfirmPassword)
    {
        b.DrawString(Game1.smallFont, L.T("text.73075237fd0f"), new Vector2(emailBox.X, emailBox.Y - 26), Game1.textColor);
        emailBox.Draw(b, false);
        if (includePassword)
        {
            b.DrawString(
                Game1.smallFont,
                includeConfirmPassword ? L.T("text.566f6ab7157d") : L.T("text.17fae5507a3f"),
                new Vector2(passwordBox.X, passwordBox.Y - 26),
                Game1.textColor);
            passwordBox.Draw(b, false);
        }
        if (includeVerification)
        {
            b.DrawString(Game1.smallFont, L.T("text.22baa091f796"), new Vector2(verificationBox.X, verificationBox.Y - 26), Game1.textColor);
            verificationBox.Draw(b, false);
        }
        if (includeConfirmPassword)
        {
            b.DrawString(Game1.smallFont, L.T("text.6fde05a916c6"), new Vector2(confirmPasswordBox.X, confirmPasswordBox.Y - 26), Game1.textColor);
            confirmPasswordBox.Draw(b, false);
        }
        if (!includeConfirmPassword)
        {
            b.DrawString(Game1.smallFont, L.T("text.edd17db6283f"), new Vector2(modelBox.X, modelBox.Y - 26), Game1.textColor);
            modelBox.Draw(b, false);
        }
    }

    private void DrawAccountTools(SpriteBatch b)
    {
        b.DrawString(Game1.smallFont, L.T("text.a8acfd2e41c3"), new Vector2(redeemBox.X, redeemBox.Y - 26), Game1.textColor);
        redeemBox.Draw(b);
        DrawButton(b, redeemButton, L.T("text.6b8e251935fd"), pendingRedeem is not null || authToken.Length == 0);
        if (CompactAccountTools)
        {
            string balance = FormatBalance();
            b.DrawString(Game1.smallFont, balance, new Vector2(emailBox.X, redeemBox.Y + 54), Game1.textColor);
            int hintX = emailBox.X + (int)Game1.smallFont.MeasureString(balance).X + 20;
            string hint = NativeDialogueFrame.FitSingleLine(L.T("text.523b7f1d570c"), Game1.smallFont, Math.Max(24, emailBox.X + emailBox.Width - hintX));
            b.DrawString(Game1.smallFont, hint, new Vector2(hintX, redeemBox.Y + 54), Game1.textColor);
        }
        else
        {
            b.DrawString(Game1.smallFont, FormatBalance(), new Vector2(emailBox.X, redeemBox.Y + 54), Game1.textColor);
            b.DrawString(Game1.smallFont, L.T("text.523b7f1d570c"), new Vector2(emailBox.X, redeemBox.Y + 78), Game1.textColor);
        }
        foreach ((int denomination, ClickableComponent button) in purchaseButtons)
            DrawButton(b, button, L.F("text.c93f57f25d7e", $"购买 ¥{HostedPurchaseCatalog.PurchaseDisplayAmount(denomination, L.Locale)}"), false);
    }

    private void ShowLogin()
    {
        mode = AuthMode.Login;
        challengeId = string.Empty;
        challengeEmail = string.Empty;
        verificationBox.Text = string.Empty;
        confirmPasswordBox.Text = string.Empty;
        status = L.T("text.b458b1aab0a0");
        statusColor = Game1.textColor;
        Reposition();
        Select(emailBox);
    }

    private void ShowRegister()
    {
        mode = AuthMode.Register;
        challengeId = string.Empty;
        challengeEmail = string.Empty;
        verificationBox.Text = string.Empty;
        status = L.T("text.327eebf8c953");
        statusColor = Game1.textColor;
        Reposition();
        Select(emailBox);
    }

    private void ShowPasswordReset()
    {
        mode = AuthMode.PasswordReset;
        challengeId = string.Empty;
        challengeEmail = string.Empty;
        verificationBox.Text = string.Empty;
        confirmPasswordBox.Text = string.Empty;
        status = L.T("text.2b6b2362eb78");
        statusColor = Game1.textColor;
        Reposition();
        Select(emailBox);
    }

    private bool CanRequestVerificationCode()
        => DateTime.UtcNow >= GetVerificationAvailableAtUtc(mode);

    private string VerificationButtonText()
    {
        int remaining = (int)Math.Ceiling((GetVerificationAvailableAtUtc(mode) - DateTime.UtcNow).TotalSeconds);
        return remaining > 0 ? L.F("text.84532584a2fe", $"重新发送 ({remaining})") : L.T("text.4656fed515ae");
    }

    private DateTime GetVerificationAvailableAtUtc(AuthMode currentMode)
        => currentMode == AuthMode.PasswordReset
            ? passwordResetVerificationAvailableAtUtc
            : registrationVerificationAvailableAtUtc;

    private void SetVerificationAvailableAtUtc(AuthMode currentMode, DateTime value)
    {
        if (currentMode == AuthMode.PasswordReset)
            passwordResetVerificationAvailableAtUtc = value;
        else if (currentMode == AuthMode.Register)
            registrationVerificationAvailableAtUtc = value;
    }

    private void SelectNextField()
    {
        object? subscriber = Game1.keyboardDispatcher.Subscriber;
        TextBox? current = subscriber as TextBox;
        bool redeemSelected = ReferenceEquals(subscriber, redeemBox);
        if (mode == AuthMode.PasswordReset)
        {
            Select(ReferenceEquals(current, emailBox)
                ? verificationBox
                : ReferenceEquals(current, verificationBox)
                    ? passwordBox
                    : ReferenceEquals(current, passwordBox) ? confirmPasswordBox : emailBox);
            return;
        }
        if (mode == AuthMode.Register)
        {
            Select(ReferenceEquals(current, emailBox)
                ? passwordBox
                : ReferenceEquals(current, passwordBox)
                    ? verificationBox
                    : ReferenceEquals(current, verificationBox) ? modelBox : emailBox);
            return;
        }
        if (redeemSelected)
        {
            Select(emailBox);
            return;
        }
        if (ReferenceEquals(current, emailBox))
            Select(passwordBox);
        else if (ReferenceEquals(current, passwordBox))
            Select(modelBox);
        else if (ReferenceEquals(current, modelBox))
            Select(redeemBox, redeemBox.X + 16);
        else
            Select(emailBox);
    }

    private void Close()
    {
        if (closed) return; closed = true; ClearKeyboard(); exitThisMenuNoSound();
    }

    private void OpenSpeech()
    {
        if (onOpenSpeech is null || pending is not null || pendingReset is not null) return;
        closed = true; ClearKeyboard(); exitThisMenuNoSound(); onOpenSpeech();
    }

    private void OpenDirectSettings()
    {
        if (onOpenDirectSettings is null)
        {
            status = L.T("text.408a15891f80");
            statusColor = Color.DarkRed;
            return;
        }

        closed = true;
        ClearKeyboard();
        exitThisMenuNoSound();
        onOpenDirectSettings();
    }

    protected override void cleanupBeforeExit() { ClearKeyboard(); base.cleanupBeforeExit(); }
    private void ClearKeyboard()
    {
        if (ReferenceEquals(Game1.keyboardDispatcher.Subscriber, emailBox)
            || ReferenceEquals(Game1.keyboardDispatcher.Subscriber, passwordBox)
            || ReferenceEquals(Game1.keyboardDispatcher.Subscriber, verificationBox)
            || ReferenceEquals(Game1.keyboardDispatcher.Subscriber, confirmPasswordBox)
            || ReferenceEquals(Game1.keyboardDispatcher.Subscriber, modelBox)
            || ReferenceEquals(Game1.keyboardDispatcher.Subscriber, redeemBox))
            Game1.keyboardDispatcher.Subscriber = null;
    }

    private void Select(TextBox box)
    {
        emailBox.Selected = ReferenceEquals(box, emailBox);
        passwordBox.Selected = ReferenceEquals(box, passwordBox);
        verificationBox.Selected = ReferenceEquals(box, verificationBox);
        confirmPasswordBox.Selected = ReferenceEquals(box, confirmPasswordBox);
        modelBox.Selected = ReferenceEquals(box, modelBox);
        redeemBox.Selected = false;
        box.SelectMe();
        Game1.keyboardDispatcher.Subscriber = box;
    }

    private void Select(CaretTextBox box, int mouseX)
    {
        emailBox.Selected = false;
        passwordBox.Selected = false;
        verificationBox.Selected = false;
        confirmPasswordBox.Selected = false;
        modelBox.Selected = false;
        redeemBox.Selected = true;
        box.SelectAt(mouseX);
        Game1.keyboardDispatcher.Subscriber = box;
    }
    private static TextBox CreateTextBox(Texture2D texture, int limit, bool password) => new(texture, null, Game1.smallFont, Game1.textColor) { textLimit = limit, PasswordBox = password, TitleText = string.Empty };
    private static bool Contains(TextBox box, int x, int y) => new Rectangle(box.X, box.Y, box.Width, box.Height).Contains(x, y);
    private static void DrawButton(SpriteBatch b, ClickableComponent component, string label, bool disabled, Color? textColor = null) { drawTextureBox(b, component.bounds.X, component.bounds.Y, component.bounds.Width, component.bounds.Height, disabled ? Color.Gray : Color.White); label = NativeDialogueFrame.FitSingleLine(label, Game1.smallFont, Math.Max(24, component.bounds.Width - 16)); Vector2 size = Game1.smallFont.MeasureString(label); b.DrawString(Game1.smallFont, label, new Vector2(component.bounds.Center.X - size.X / 2f, component.bounds.Center.Y - size.Y / 2f), disabled ? Color.DarkGray : textColor ?? Game1.textColor); }
}
