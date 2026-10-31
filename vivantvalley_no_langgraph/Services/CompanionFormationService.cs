using Microsoft.Xna.Framework;
using StardewValley;

namespace VivantValley.Services;

/// <summary>Maintains a compact follower formation behind a mounted player.</summary>
public sealed class CompanionFormationService
{
    // The player and the mount occupy most of one tile. Keep the first NPC
    // close enough to look seated with the player, then leave enough depth
    // between followers to avoid completely hiding their sprites.
    private const float FirstFollowerOffsetPixels = 42f;
    private const float FollowerSpacingPixels = 42f;
    private const float HorizontalSeatLiftPixels = 12f;
    private const float MountedSitDrawLiftPixels = 56f;
    private const double SitFrameDurationMilliseconds = 100d;
    private static readonly string[] SitAnimationSuffixes =
    {
        "_sit",
        "_sit_down",
        "_sit_left",
        "_sit_up",
    };

    private readonly Dictionary<string, FormationMember> members = new(StringComparer.Ordinal);
    private long nextOrder;

    public void Register(NPC npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        members[npc.Name] = new FormationMember(npc, nextOrder++);
    }

    public void Unregister(string? npcName)
    {
        if (!string.IsNullOrWhiteSpace(npcName))
        {
            if (members.TryGetValue(npcName, out FormationMember? member))
                ReleaseMountedPose(member);
            members.Remove(npcName);
        }
    }

    /// <summary>
    /// Place the NPC behind the mounted player and return whether mounted
    /// formation handling took over this tick.
    /// </summary>
    public bool TryApplyMountedPosition(NPC npc, Farmer leader)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(leader);

        if (leader.mount is null
            || !leader.isRidingHorse()
            || leader.currentLocation is null
            || !ReferenceEquals(npc.currentLocation, leader.currentLocation))
        {
            if (leader.mount is null || !leader.isRidingHorse())
            {
                foreach (FormationMember candidate in members.Values)
                {
                    candidate.WasMounted = false;
                    ReleaseMountedPose(candidate);
                }
            }
            return false;
        }

        if (!members.TryGetValue(npc.Name, out FormationMember? member)
            || !ReferenceEquals(member.Npc, npc))
        {
            Register(npc);
            member = members[npc.Name];
        }

        List<FormationMember> activeMembers = members.Values
            .Where(candidate => candidate.Npc.currentLocation is not null
                                && ReferenceEquals(candidate.Npc.currentLocation, leader.currentLocation))
            .OrderBy(candidate => candidate.Order)
            .ToList();
        int index = activeMembers.FindIndex(candidate => ReferenceEquals(candidate.Npc, npc));
        if (index < 0)
            return false;

        Vector2 backward = leader.FacingDirection switch
        {
            Game1.up => new Vector2(0f, 1f),
            Game1.right => new Vector2(-1f, 0f),
            Game1.down => new Vector2(0f, -1f),
            Game1.left => new Vector2(1f, 0f),
            _ => new Vector2(0f, 1f),
        };

        // Farmer.Position and Horse.Position are sprite origins, not the
        // actual seat centers. Use both collision-box centers so this remains
        // correct for vanilla horses and Tractor Mod's Horse-based tractor.
        Rectangle riderBounds = leader.GetBoundingBox();
        Rectangle mountBounds = leader.mount.GetBoundingBox();
        Vector2 riderOffsetFromMount = riderBounds.Center.ToVector2() - mountBounds.Center.ToVector2();
        Vector2 anchor = mountBounds.Center.ToVector2() + riderOffsetFromMount;
        float offset = FirstFollowerOffsetPixels + index * FollowerSpacingPixels;
        Vector2 seatLift = leader.FacingDirection is Game1.left or Game1.right
            ? new Vector2(0f, -HorizontalSeatLiftPixels)
            : Vector2.Zero;
        Vector2 targetCenter = anchor + backward * offset + seatLift;

        // NPC collision boxes can have a small origin offset. Preserve that
        // offset while moving the NPC by its center, instead of aligning the
        // sprite's top-left corner with the mount's top-left corner.
        Rectangle npcBounds = npc.GetBoundingBox();
        Vector2 npcCenterOffset = npcBounds.Center.ToVector2() - npc.Position;
        Vector2 target = targetCenter - npcCenterOffset;

        // Mounted formation owns the NPC for this tick, so clear any path
        // controller before setting its pixel position directly.
        npc.controller = null;
        npc.temporaryController = null;
        bool isBeginningMountedPose = !member.WasMounted;
        if (isBeginningMountedPose)
            CaptureVisualState(member);
        npc.Halt();
        npc.Position = target;
        npc.faceDirection(leader.FacingDirection);
        if (isBeginningMountedPose)
        {
            member.WasMounted = true;
            if (!TryBeginMountedSit(member))
                npc.jump();
            npc.doEmote(Character.exclamationEmote, nextEventCommand: false);
        }
        else if (!member.UsingSitPose
                 && !member.HoldingAtApex
                 && npc.yJumpOffset < 0
                 && npc.yJumpVelocity <= 0f)
        {
            // Keep the one-time boarding jump at its apex while the NPC is
            // visually seated behind the mounted player.
            member.HoldingAtApex = true;
            member.MountedJumpOffset = npc.yJumpOffset;
            npc.yJumpVelocity = 0f;
            npc.yJumpGravity = 0f;
        }
        if (member.UsingSitPose)
        {
            ApplyMountedSit(member);
        }
        else if (member.HoldingAtApex)
        {
            npc.yJumpOffset = member.MountedJumpOffset;
            npc.yJumpVelocity = 0f;
            npc.yJumpGravity = 0f;
        }
        return true;
    }

    private static void CaptureVisualState(FormationMember member)
    {
        member.OriginalFrame = member.Npc.Sprite.CurrentFrame;
        member.OriginalDrawOffset = member.Npc.drawOffset;
        member.OriginalHideShadow = member.Npc.HideShadow;
    }

    private static bool TryBeginMountedSit(FormationMember member)
    {
        try
        {
            NPC npc = member.Npc;
            Dictionary<string, string> animations = DataLoader.AnimationDescriptions(Game1.content);
            string npcName = npc.Name.ToLowerInvariant();
            string? description = SitAnimationSuffixes
                .Select(suffix => npcName + suffix)
                .Select(animationName => animations.TryGetValue(animationName, out string? value) ? value : null)
                .FirstOrDefault(value => value is not null);
            if (description is null)
                return false;

            string[] sections = description.Split('/');
            if (sections.Length < 2 || npc.Sprite.SpriteWidth <= 0 || npc.Sprite.SpriteHeight <= 0)
                return false;

            int maximumFrame = npc.Sprite.Texture.Width / npc.Sprite.SpriteWidth
                               * (npc.Sprite.Texture.Height / npc.Sprite.SpriteHeight);
            int[] frames = Utility.parseStringToIntArray(sections[1])
                .Where(frame => frame >= 0 && frame < maximumFrame)
                .ToArray();
            if (frames.Length == 0)
                return false;

            member.UsingSitPose = true;
            member.SitFrames = frames;
            member.SitStartedAtMilliseconds = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0d;
            npc.yJumpOffset = 0;
            npc.yJumpVelocity = 0f;
            npc.yJumpGravity = -0.5f;
            return true;
        }
        catch
        {
            // Sitting is optional; malformed third-party animation data falls
            // back to the existing mounted jump pose.
            return false;
        }
    }

    private static void ApplyMountedSit(FormationMember member)
    {
        NPC npc = member.Npc;
        int[] frames = member.SitFrames;
        double now = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds
                     ?? member.SitStartedAtMilliseconds;
        int frameIndex = (int)((now - member.SitStartedAtMilliseconds) / SitFrameDurationMilliseconds)
                         % frames.Length;

        npc.Sprite.CurrentFrame = frames[Math.Max(0, frameIndex)];
        npc.Sprite.UpdateSourceRect();
        npc.drawOffset = member.OriginalDrawOffset + new Vector2(0f, -MountedSitDrawLiftPixels);
        npc.HideShadow = true;
        npc.yJumpOffset = 0;
        npc.yJumpVelocity = 0f;
        npc.yJumpGravity = -0.5f;
    }

    private static void ReleaseMountedPose(FormationMember member)
    {
        if (member.UsingSitPose)
        {
            member.UsingSitPose = false;
            member.SitFrames = Array.Empty<int>();
            member.Npc.drawOffset = member.OriginalDrawOffset;
            member.Npc.HideShadow = member.OriginalHideShadow;
            member.Npc.Sprite.CurrentFrame = member.OriginalFrame;
            member.Npc.Sprite.UpdateSourceRect();
            member.Npc.yJumpOffset = 0;
            member.Npc.yJumpVelocity = 0f;
            member.Npc.yJumpGravity = -0.5f;
        }

        ReleaseMountedJump(member);
    }

    private static void ReleaseMountedJump(FormationMember member)
    {
        if (!member.HoldingAtApex)
            return;

        member.HoldingAtApex = false;
        member.MountedJumpOffset = 0;
        member.Npc.yJumpOffset = 0;
        member.Npc.yJumpVelocity = 0f;
        member.Npc.yJumpGravity = -0.5f;
    }

    private sealed class FormationMember
    {
        public FormationMember(NPC npc, long order)
        {
            Npc = npc;
            Order = order;
        }

        public NPC Npc { get; }

        public long Order { get; }

        public bool WasMounted { get; set; }

        public bool HoldingAtApex { get; set; }

        public int MountedJumpOffset { get; set; }

        public bool UsingSitPose { get; set; }

        public int[] SitFrames { get; set; } = Array.Empty<int>();

        public double SitStartedAtMilliseconds { get; set; }

        public int OriginalFrame { get; set; }

        public Vector2 OriginalDrawOffset { get; set; }

        public bool OriginalHideShadow { get; set; }
    }
}
