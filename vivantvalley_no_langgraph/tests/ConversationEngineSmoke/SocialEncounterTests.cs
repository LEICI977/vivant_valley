using VivantValley;
using VivantValley.Services;

internal static class SocialEncounterTests
{
    public static void Run()
    {
        TestModeSwitchDuringEncounter();
        TestRefreshPreservesProgress();
        TestChoicesAndRepeatedClicks();
        TestFailureMessages();
        TestMenuCancellation();
        Console.WriteLine("Social encounter refresh, decline, delivery, duplicate and failure tests passed.");
    }

    private static DailySocialPlan Plan(bool controller)
        => new DailySocialPlanner().CreatePlan("save", "player", 100,
            new[] { new SocialPlanningCandidate { NpcName = "Leah", VanillaHearts = 6 } },
            new DailySocialPlannerOptions
            {
                PlannerVersion = 4, MinimumCandidates = 1, MaximumCandidates = 1,
                ControllerMode = controller, RequireRecentPositiveConversation = false,
            });

    private static void TestModeSwitchDuringEncounter()
    {
        foreach (var status in new[] { DailySocialCandidateStatus.Generating,
                     DailySocialCandidateStatus.Ready, DailySocialCandidateStatus.Presenting })
        {
            DailySocialPlan old = Plan(true);
            var shown = old.Candidates[0];
            shown.Status = status;
            DailySocialPlan replacement = Plan(false);
            Check(replacement.Candidates.Single(c => c.ActionId == shown.ActionId).Status
                == DailySocialCandidateStatus.Planned, "Original stale-window reproduction changed.");
            Check(SocialPlanRefreshPolicy.ShouldDefer(old, 100, false), "Mode switch replaced an in-flight encounter.");
            DailySocialPlan current = SocialPlanRefreshPolicy.ShouldDefer(old, 100, false) ? old : replacement;
            Check(ReferenceEquals(current.Candidates[0], shown) && shown.Status == status,
                "Displayed encounter identity was lost.");
            shown.Status = DailySocialCandidateStatus.Completed;
            Check(!SocialPlanRefreshPolicy.ShouldDefer(old, 100, false), "Refresh never resumes after completion.");
            Check(SocialPlanRefreshPolicy.ShouldDefer(old, 100, true), "Live window must defer refresh even if its record is stale.");
            Check(!SocialPlanRefreshPolicy.ShouldDefer(old, 101, true), "Yesterday's window prevented a new day plan.");
        }
    }

    private static void TestRefreshPreservesProgress()
    {
        var old = Plan(true);
        var completed = old.Candidates[0];
        completed.Status = DailySocialCandidateStatus.Completed;
        DailySocialPlanner.CloseSiblingOpportunities(old, "Leah", completed.ActionId);
        old.TriggeredCount = 1;
        old.GiftCount = 2; // Includes a gift from ordinary chat.
        var states = new Dictionary<string, NpcSocialState>(StringComparer.OrdinalIgnoreCase)
        {
            ["Leah"] = new() { NpcName = "Leah", CompletedActionIds = new() { completed.ActionId } },
        };
        foreach (bool includeNpc in new[] { true, false })
        {
            var next = Plan(false);
            if (!includeNpc) next.Candidates.Clear();
            SocialPlanRefreshPolicy.PreserveProgress(old, next, states);
            next.Normalize();
            Check(next.GiftCount == 2 && next.TriggeredCount == 1, "Same-day counters were reset.");
            Check(next.Candidates.Single(c => c.ActionId == completed.ActionId).Status
                == DailySocialCandidateStatus.Completed, "Completed action was dropped.");
            Check(!next.Candidates.Any(c => c.NpcName == "Leah" && c.Status == DailySocialCandidateStatus.Planned),
                "Rebuilding reopened a sibling opportunity.");
        }
        var tomorrow = Plan(false);
        tomorrow.Day = 101;
        SocialPlanRefreshPolicy.PreserveProgress(old, tomorrow, states);
        Check(tomorrow.GiftCount == 0 && tomorrow.TriggeredCount == 0, "Daily counters leaked into tomorrow.");
    }

    private static void TestChoicesAndRepeatedClicks()
    {
        int deliveries = 0, completions = 0;
        bool? accepted = null;
        var candidate = new DailySocialCandidate { Status = DailySocialCandidateStatus.Presenting };
        bool Complete(bool value)
        {
            accepted = value;
            completions++;
            candidate.Status = DailySocialCandidateStatus.Completed;
            return true;
        }
        EncounterChoiceResult Deliver() { deliveries++; return EncounterChoiceResult.Completed; }
        EncounterChoiceResult Resolve(string choice) => SocialEncounterChoiceResolver.Resolve(
            candidate, false, true, choice, Deliver, Complete);

        Check(Resolve("decline") == EncounterChoiceResult.Completed && accepted == false
            && completions == 1 && deliveries == 0, "Declining touched inventory or gift validation.");
        Check(Resolve("accept") == EncounterChoiceResult.AlreadyCompleted && deliveries == 0,
            "Accept after decline delivered a gift.");

        candidate.Status = DailySocialCandidateStatus.Presenting;
        Check(Resolve("accept") == EncounterChoiceResult.Completed && accepted == true && deliveries == 1,
            "Valid acceptance failed.");
        Check(Resolve("accept") == EncounterChoiceResult.AlreadyCompleted && deliveries == 1,
            "Repeated acceptance delivered twice.");

        foreach (string choice in new[] { "accept", "decline" })
        {
            candidate.Status = DailySocialCandidateStatus.Planned;
            var stale = Resolve(choice);
            Check(stale == EncounterChoiceResult.SceneExpired && stale.ClosesMenu(), "Stale window trapped a choice.");
        }
        Check(deliveries == 1, "An expired encounter delivered a gift.");

        candidate.Status = DailySocialCandidateStatus.Presenting;
        foreach (var failure in new[] { EncounterChoiceResult.GiftUnavailable, EncounterChoiceResult.DeliveryFailed })
        {
            var result = SocialEncounterChoiceResolver.Resolve(candidate, false, true, "accept",
                () => failure, _ => throw new Exception("Failed delivery was completed."));
            Check(result == failure && !result.ClosesMenu(), "Delivery failure category was lost.");
            result = SocialEncounterChoiceResolver.Resolve(candidate, false, true, "decline",
                () => throw new Exception("Decline retried gift validation."), _ => true);
            Check(result == EncounterChoiceResult.Completed, "Decline failed after a delivery error.");
        }
        var settlement = SocialEncounterChoiceResolver.Resolve(candidate, false, true, "accept", Deliver, _ => false);
        Check(settlement == EncounterChoiceResult.SettlementFailed && settlement.ClosesMenu(),
            "Delivered gift remained claimable after settlement failure.");
        var missing = SocialEncounterChoiceResolver.Resolve(null, false, true, "decline", Deliver, Complete);
        Check(missing == EncounterChoiceResult.SceneExpired && missing.ClosesMenu(), "Missing record trapped decline.");
        var duplicate = SocialEncounterChoiceResolver.Resolve(candidate, true, true, "accept", Deliver, Complete);
        Check(duplicate == EncounterChoiceResult.AlreadyCompleted, "Completion ledger was ignored.");
    }

    private static void TestFailureMessages()
    {
        foreach (string locale in new[] { "zh", "en" })
        using (L.BeginRequest(locale))
        {
            foreach (EncounterChoiceResult result in Enum.GetValues<EncounterChoiceResult>())
            {
                if (result == EncounterChoiceResult.Completed) continue;
                string key = result.MessageKey();
                string message = L.T(key);
                Check(message != key && message.Length > 0, "Missing localized encounter message.");
                if (result != EncounterChoiceResult.InventoryFull)
                    Check(!message.Contains("背包") && !message.Contains("inventory", StringComparison.OrdinalIgnoreCase),
                        "A non-inventory failure still blames the backpack.");
            }
        }
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private static void TestMenuCancellation()
    {
        // Exercise the real menu's cancellation without constructing fonts, textures or audio.
        var type = typeof(VivantValley.Menus.AiProactiveEncounterMenu);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic;
        foreach (bool failCancel in new[] { false, true })
        {
            var menu = (VivantValley.Menus.AiProactiveEncounterMenu)
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            int cancels = 0, closes = 0;
            var writer = new DialogueTypewriter();
            writer.SetText("Still typing a long gift offer...");
            Check(!writer.IsComplete, "Test must cancel during text playback.");
            type.GetField("typewriter", flags)!.SetValue(menu, writer);
            type.GetField("onCancel", flags)!.SetValue(menu, (Action)(() =>
            {
                cancels++;
                if (failCancel) throw new InvalidOperationException("simulated cancellation failure");
            }));
            type.GetField("onClosed", flags)!.SetValue(menu, (Action)(() => closes++));
            var cancel = type.GetMethod("CancelEncounter", flags)!;
            try { cancel.Invoke(menu, null); }
            catch (System.Reflection.TargetInvocationException ex)
                when (failCancel && ex.InnerException?.Message == "simulated cancellation failure") { }
            Check(cancels == 1 && closes == 1 && (bool)type.GetField("closed", flags)!.GetValue(menu)!,
                "Escape did not close the menu and release its owner.");
            cancel.Invoke(menu, null);
            Check(cancels == 1 && closes == 1, "Repeated Escape repeated cancellation or cleanup.");
        }
    }
}
