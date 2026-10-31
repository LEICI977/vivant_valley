using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley.Menus;

namespace VivantValley.Services;

/// <summary>Form values must not be shortened to fit the hidden vanilla text box.</summary>
internal static class MobileTextField
{
    public static TextBox Create(Texture2D texture, SpriteFont font, Color color, int limit, bool password)
        => new(texture, null, font, color)
        {
            textLimit = limit,
            limitWidth = false,
            PasswordBox = password,
            Selected = false,
            TitleText = string.Empty,
        };
}
