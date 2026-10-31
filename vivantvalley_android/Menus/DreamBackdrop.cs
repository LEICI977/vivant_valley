using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Services;

namespace VivantValley.Menus;

/// <summary>Local, disposable collage of existing sharing stickers; no fixed map or live actors move.</summary>
internal sealed class DreamBackdrop : IDisposable
{
    private sealed record Fragment(Texture2D Texture, float X, float Y, float Size, float Rotation, float Phase, float Alpha);
    private readonly List<Fragment> fragments = new();
    private Texture2D? glow;
    private readonly DreamPalette palette;
    private static Color Rgb(int rgb) => new((rgb >> 16) & 255,(rgb >> 8) & 255,rgb & 255);
    public DreamBackdrop(string directory, string seed, string npc, Action<string> warn)
    {
        palette = DreamPalettes.Select(npc,seed);
        try
        {
            var assets = JsonSerializer.Deserialize<DreamCollageAsset[]>(File.ReadAllText(Path.Combine(directory,"assets","dreams","catalog.json")))
                ?? Array.Empty<DreamCollageAsset>();
            var selected = DreamCollage.Select(assets,seed);
            var positions = DreamCollage.Positions(seed,selected.Length);
            for (int i = 0; i < selected.Length; i++)
            {
                Texture2D? texture = null;
                try
                {
                    string root = Path.GetFullPath(Path.Combine(directory,"assets","share","images")) + Path.DirectorySeparatorChar;
                    string path = Path.GetFullPath(Path.Combine(directory,selected[i].Path));
                    if (!path.StartsWith(root,StringComparison.Ordinal) || new FileInfo(path).Length > 1024 * 1024) continue;
                    using var stream = File.OpenRead(path);
                    texture = Texture2D.FromStream(Game1.graphics.GraphicsDevice,stream);
                    if (texture.Width > 512 || texture.Height > 512) { texture.Dispose(); continue; }
                    // PNG is straight alpha; SpriteBatch uses premultiplied alpha.
                    var pixels = new Color[texture.Width * texture.Height];
                    texture.GetData(pixels);
                    for (int n = 0; n < pixels.Length; n++)
                    { var c = pixels[n]; pixels[n] = new Color(c.R*c.A/255,c.G*c.A/255,c.B*c.A/255,(int)c.A); }
                    texture.SetData(pixels);
                    float U(string property) => (float)DreamCollage.Unit(seed,i,property);
                    fragments.Add(new(texture,positions[i].X,positions[i].Y,64+U("size")*98,
                        (U("rotation")-.5f)*1.2f,U("phase")*6.28f,.30f+U("alpha")*.32f));
                }
                catch (Exception ex) { texture?.Dispose(); warn("Dream sticker unavailable: " + ex.GetType().Name); }
            }
            glow = new Texture2D(Game1.graphics.GraphicsDevice,64,64);
            var light = new Color[64*64];
            for (int y=0;y<64;y++) for(int x=0;x<64;x++)
            {
                float r = Vector2.Distance(new Vector2(x,y),new Vector2(31.5f))/32;
                light[y*64+x] = Color.White * MathF.Pow(Math.Max(0,1-r),2);
            }
            glow.SetData(light);
        }
        catch (Exception ex) { warn("Dream collage unavailable: " + ex.GetType().Name); }
    }
    public void Draw(SpriteBatch b, NPC? npc, double seconds)
    {
        int width = Game1.uiViewport.Width, height = Game1.uiViewport.Height;
        float scale = Math.Min(width/1280f,height/720f);
        Color top=Rgb(palette.Top), middle=Rgb(palette.Middle), bottom=Rgb(palette.Bottom);
        // Slow light breathing within a chosen palette; no sudden hue jumps or per-turn rerolls.
        float horizon=.47f+(float)Math.Sin(seconds*.10)*.045f;
        for(int y=0;y<height;y+=8)
        {
            float t=y/(float)Math.Max(1,height);
            Color colour=t<horizon ? Color.Lerp(top,middle,t/horizon)
                : Color.Lerp(middle,bottom,(t-horizon)/(1-horizon));
            b.Draw(Game1.staminaRect,new Rectangle(0,y,width,Math.Min(8,height-y)),colour);
        }
        if (glow is not null)
            for(int i=0;i<5;i++)
                b.Draw(glow,new Vector2(width*(.12f+i*.20f)+(float)Math.Sin(seconds*.08+i)*width*.025f,
                    height*(.18f+(i%2)*.22f)+(float)Math.Sin(seconds*.12+i)*height*.018f),null,
                    Rgb(i%2==0 ? palette.GlowA : palette.GlowB)*.38f,
                    0,new Vector2(32),new Vector2(width/110f,height/135f),SpriteEffects.None,.1f);
        foreach(var f in fragments)
        {
            var position = new Vector2(width*f.X+(float)Math.Sin(seconds*.14+f.Phase)*14*scale,
                height*f.Y+(float)Math.Sin(seconds*.30+f.Phase)*12*scale);
            float size = f.Size*scale/Math.Max(f.Texture.Width,f.Texture.Height);
            // Soft ghost trails give unrelated familiar objects a weightless dreamlike look.
            for(int trail=2;trail>=0;trail--)
                b.Draw(f.Texture,position+new Vector2(-12*trail,6*trail)*scale,null,
                    Rgb(palette.Tint)*(trail==0 ? f.Alpha : .045f),
                    f.Rotation+(float)Math.Sin(seconds*.18+f.Phase)*.12f,
                    new Vector2(f.Texture.Width/2f,f.Texture.Height/2f),size,SpriteEffects.None,.2f);
        }
        for(int i=0;i<48;i++)
        {
            float x=((i*97+(float)seconds*(2+i%3))%(width+20))-10;
            float y=height*(.06f+(i*37%52)/100f)+(float)Math.Sin(seconds*.4+i)*5*scale;
            int size=Math.Max(1,(int)((i%3+1)*scale));
            b.Draw(Game1.staminaRect,new Rectangle((int)x,(int)y,size,size),Rgb(palette.Stars)*(.18f+.14f*(float)(1+Math.Sin(seconds*.6+i))));
        }
        int menuTop = Game1.activeClickableMenu is DialogueBox dialogue ? dialogue.y
            : Game1.activeClickableMenu?.yPositionOnScreen ?? height;
        float floor = Math.Clamp(menuTop-28,Math.Min(160,height*.4f),height*.59f);
        float actorScale = Math.Min(1f,Math.Min(width/1000f,Math.Max(.45f,floor/240f)));
        float xPlayer=width*.45f-32*actorScale,xNpc=width*.55f-32*actorScale;
        // Explicit draw overload avoids changing the real sleeping farmer's animation.
        Game1.player.FarmerRenderer.draw(b,new FarmerSprite.AnimationFrame(0,100),0,new Rectangle(0,0,16,32),
            new Vector2(xPlayer,floor-128*actorScale),Vector2.Zero,.8f,2,Color.White*.9f,0f,actorScale,Game1.player);
        if(npc is not null)
            b.Draw(npc.Sprite.Texture,new Vector2(xNpc,floor-128*actorScale),new Rectangle(0,0,16,32),
                Color.White*.92f,0,Vector2.Zero,4*actorScale,SpriteEffects.None,.8f);
        if(seconds<.7) b.Draw(Game1.staminaRect,new Rectangle(0,0,width,height),Color.Black*(float)(1-seconds/.7));
    }
    public void Dispose()
    {
        foreach(var fragment in fragments) fragment.Texture.Dispose();
        fragments.Clear(); glow?.Dispose(); glow=null;
    }
}
