namespace VivantValley.Services;

public static class WeeklyStoryMailService
{
    public static string Build(WeeklyStoryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string body = GiftMailContentService.Sanitize(
            plan.MailBody,
            L.F("text.e88458bd8222", $"这周来{plan.LocationDisplayName}找我吧，我有件事想当面告诉你。"),
            900);
        string sender = GiftMailContentService.Sanitize(
            plan.Actors.FirstOrDefault(actor => actor.Name.Equals(plan.Inviter, StringComparison.OrdinalIgnoreCase))?.DisplayName,
            L.T("text.4781f6b2a158"),
            80);
        return $"{body}^^{DescribeSchedule(plan)}^^-{sender}";
    }

    public static string DescribeSchedule(WeeklyStoryPlan plan)
        => WeeklyStoryInvitationPolicy.IsFlexible(plan)
            ? (L.IsChinese
                ? $"有空时来{plan.LocationDisplayName}聊聊吧，不限时段。有效至{DescribeDate(plan.ExpiryDay)}。"
                : $"Stop by {plan.LocationDisplayName} when you're free, any time of day. Available through {DescribeDate(plan.ExpiryDay)}.")
            : L.IsChinese
            ? $"日期：{DescribeDate(plan.ScheduledDay)}至{DescribeDate(plan.ExpiryDay)}；地点：{(string.IsNullOrWhiteSpace(plan.LocationDisplayName) ? plan.LocationId : plan.LocationDisplayName)}；时间：{DescribeTimeSlot(plan.TimeSlot)}，每日 {plan.StartTime / 100:D2}:{plan.StartTime % 100:D2}–{plan.EndTime / 100:D2}:{plan.EndTime % 100:D2}"
            : $"Date: {DescribeDate(plan.ScheduledDay)}–{DescribeDate(plan.ExpiryDay)}; Place: {(string.IsNullOrWhiteSpace(plan.LocationDisplayName) ? plan.LocationId : plan.LocationDisplayName)}; {DescribeTimeSlot(plan.TimeSlot)}, daily {plan.StartTime / 100:D2}:{plan.StartTime % 100:D2}–{plan.EndTime / 100:D2}:{plan.EndTime % 100:D2}";

    public static string DescribeTimeSlot(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "anytime" => L.IsChinese ? "不限时段" : "Any time",
            "daylight" => L.IsChinese ? "白天" : "Daylight",
            "sunset" => L.IsChinese ? "傍晚" : "Sunset",
            WeeklyStoryTimeSlots.Morning => L.T("text.298336ec2c0e"),
            WeeklyStoryTimeSlots.Afternoon => L.T("text.af81dcf37aa8"),
            WeeklyStoryTimeSlots.Night => L.T("text.30003ad423e4"),
            _ => L.T("text.1bd701b26439"),
        };

    public static string DescribeDate(int totalDays)
    {
        int day = Math.Max(0, totalDays);
        int year = day / 112 + 1;
        int seasonIndex = day % 112 / 28;
        int dayOfMonth = day % 28 + 1;
        string season = seasonIndex switch
        {
            0 => L.T("text.8ac692425ee1"),
            1 => L.T("text.6e9abee0fe97"),
            2 => L.T("text.9fad05bcc26f"),
            _ => L.T("text.0ae780e1bafa"),
        };
        return L.F("text.1dac24586900", $"第{year}年{season}{dayOfMonth}日");
    }
}
