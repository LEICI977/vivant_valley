using Microsoft.Xna.Framework;
using VivantValley.Menus;

internal static class NativeReplyTests
{
    public static void Run()
    {
        Check(NativeReplyPolicy.Actions(ReplyPhase.Generating, false, true, true).SequenceEqual(new[] { ReplyAction.Cancel }),
            "Generation must offer only cancellation, never a premature reply/action.");
        Check(NativeReplyPolicy.Actions(ReplyPhase.Reply, false, true, true).SequenceEqual(new[] { ReplyAction.End }),
            "Unread pages exposed continuation, sharing or personality editing.");
        Check(NativeReplyPolicy.Actions(ReplyPhase.Reply, true, true, true).SequenceEqual(
            new[] { ReplyAction.Personality, ReplyAction.Share, ReplyAction.Continue, ReplyAction.End }), "Final reply toolbar order is wrong.");
        Check(NativeReplyPolicy.Actions(ReplyPhase.Reply, true, true, false).SequenceEqual(
            new[] { ReplyAction.Personality, ReplyAction.Continue, ReplyAction.End }), "Mobile toolbar exposed unsupported sharing.");
        Check(NativeReplyPolicy.Actions(ReplyPhase.Confirmation, false, true, true).SequenceEqual(new[] { ReplyAction.Decline }),
            "An unread confirmation can be accepted.");
        Check(NativeReplyPolicy.Actions(ReplyPhase.Confirmation, true, true, true).SequenceEqual(new[] { ReplyAction.Accept, ReplyAction.Decline }),
            "Confirmation mixed unrelated actions into explicit choices.");
        Check(NativeReplyPolicy.Actions(ReplyPhase.Error, true, true, true).SequenceEqual(new[] { ReplyAction.Return, ReplyAction.End }),
            "Failure offered an automatic resend or continuation of a nonexistent reply.");
        Check(NativeReplyPolicy.Actions(ReplyPhase.Closed, true, true, true).Length == 0, "Closed menus can fire a second action.");
        Check(NativeReplyPolicy.Actions(ReplyPhase.Closing, true, true, true).Length == 0,
            "Closing replies expose buttons which can restart or interrupt handoff.");
        TestComposerHandoff();
        foreach (bool last in new[] {false,true})
        {
            Check(NativeReplyPolicy.Read(true, false, last, false) == ReplyReadAction.None, "Opening animation accepted a reading click.");
            Check(NativeReplyPolicy.Read(false, false, last, false) == ReplyReadAction.Reveal, "Typing click did not reveal this page only.");
            Check(NativeReplyPolicy.Read(false, true, last, true) == ReplyReadAction.None, "Rapid reading bypassed native safety delay.");
        }
        Check(NativeReplyPolicy.Read(false, true, false, false) == ReplyReadAction.NextPage, "Completed non-final page cannot advance.");
        Check(NativeReplyPolicy.Read(false, true, true, false) == ReplyReadAction.None, "Final page can enter vanilla close callbacks.");

        Check(NativeReplyPolicy.ReadScene(true,false,false)==ReplyReadAction.None,"Scene Enter bypassed opening animation.");
        Check(NativeReplyPolicy.ReadScene(true,true,false)==ReplyReadAction.None,"Scene Enter bypassed closing animation.");
        Check(NativeReplyPolicy.ReadScene(false,false,true)==ReplyReadAction.Reveal,"Scene Enter should reveal typing without closing.");
        Check(NativeReplyPolicy.ReadScene(false,true,true)==ReplyReadAction.None,"Scene Enter skipped the native safety delay.");
        Check(NativeReplyPolicy.ReadScene(false,true,false)==ReplyReadAction.NextPage,"Scene Enter cannot advance/finish a fully read page.");

        var normal = NativeReplyLayout.Create(new Rectangle(0,0,1920,1080),1296,456,44);
        Check(normal.Scale == 1f, "Reply must retain native scale when the screen has room.");
        foreach (Point size in new[] {new Point(360,640),new Point(640,360),new Point(800,360),new Point(1920,1080),new Point(400,300)})
        foreach (int control in new[] {44,64,96})
        foreach (Point native in new[] {new Point(1296,456),new Point(700,140),new Point(1600,800)})
        {
            Rectangle safe = new(20,20,size.X-40,size.Y-40);
            var layout = NativeReplyLayout.Create(safe,native.X,native.Y,control);
            Check(safe.Contains(layout.Frame) && safe.Contains(layout.Toolbar), "Native frame or toolbar escaped safe viewport.");
            Check(!layout.Frame.Intersects(layout.Toolbar), "Actions overlap native reply text/portrait.");
            Check(Math.Abs(layout.Frame.Width/(float)native.X-layout.Frame.Height/(float)native.Y)<.01,
                "Native portrait/text was distorted instead of uniformly scaled.");
            foreach (int[] widths in new[] {new[]{180},new[]{160,160},new[]{180,140,180},new[]{190,100,180,180}})
            {
                Rectangle[] buttons=layout.Buttons(widths);
                for(int i=0;i<buttons.Length;i++)
                {
                    Check(layout.Toolbar.Contains(buttons[i]) && !layout.Frame.Intersects(buttons[i]), "Toolbar button overlaps reply or escapes safe area.");
                    if(i>0) Check(!buttons[i].Intersects(buttons[i-1]) && buttons[i].Y==buttons[0].Y,
                        "Buttons overlap or wrap onto a second row.");
                }
            }
        }
        Console.WriteLine("Native reply reading, confirmation, final-page and toolbar layout tests passed.");
    }

    private static void TestComposerHandoff()
    {
        Rectangle body = new(73, 210, 1200, 384);
        var transition = new ReplyHandoffTransition();
        Check(!transition.Advance(TimeSpan.FromSeconds(1), true), "Idle reply can open a composer.");
        Check(transition.Begin(body.Width) && !transition.Begin(body.Width), "Repeated Enter restarted the outro.");
        Check(transition.Bounds(body) == body, "Outro starts with a flash or a new box.");
        Check(!transition.Advance(TimeSpan.FromMilliseconds(100), true), "Composer opened before the shrinking frame finished.");
        var half = transition.Bounds(body);
        Check(half.Width == 600 && half.Height == 192 && half.Center == body.Center,
            "Outro does not preserve vanilla speed, centre and proportions.");
        Check(!transition.Begin(body.Width) && transition.Bounds(body) == half,
            "A second click reset the partially closed reply.");
        Check(transition.Advance(TimeSpan.FromMilliseconds(100), true) && transition.Remaining == 0,
            "Completed outro did not hand off to the composer.");
        Check(!transition.Advance(TimeSpan.FromMilliseconds(100), true) && !transition.Begin(body.Width),
            "Continuation fired twice.");
        foreach (bool cancelExplicitly in new[] { false, true })
        {
            var interrupted = new ReplyHandoffTransition();
            interrupted.Begin(body.Width);
            interrupted.Advance(TimeSpan.FromMilliseconds(50), true);
            if (cancelExplicitly) interrupted.Cancel();
            Check(!interrupted.Advance(TimeSpan.FromSeconds(1), ownsMenu: false)
                && !interrupted.Advance(TimeSpan.FromSeconds(1), ownsMenu: true),
                "Dismissal or another mod's menu still opened a stale composer.");
        }
        foreach (double frameMs in new[] { 8.333, 16.667, 33.333, 1000d })
        {
            var fps = new ReplyHandoffTransition();
            fps.Begin(body.Width);
            int callbacks = 0;
            float previous = 1;
            for (int i = 0; i < 100; i++)
            {
                if (fps.Advance(TimeSpan.FromMilliseconds(frameMs), true)) callbacks++;
                Check(fps.Remaining <= previous && fps.Remaining >= 0, "Outro expanded or overshot at a different frame rate.");
                previous = fps.Remaining;
            }
            Check(callbacks == 1, "Low/high FPS repeated or lost the handoff.");
        }
        Console.WriteLine("Native reply outro, one-shot handoff, repeated input and interrupted ownership passed.");
    }
    private static void Check(bool value,string message)
    {if(!value)throw new InvalidOperationException(message);}
}
