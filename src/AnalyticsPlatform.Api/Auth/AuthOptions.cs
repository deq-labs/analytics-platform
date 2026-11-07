namespace AnalyticsPlatform.Api.Auth;

// Bound from the "Auth" config section.
public sealed class AuthOptions
{
    // false (default): self-hosting. After the first account (the admin), new
    // accounts need an invite. true: anyone can sign up (SaaS).
    public bool OpenRegistration { get; set; }

    // Per client IP, across login + register. Slows password guessing.
    public int AttemptsPerMinute { get; set; } = 10;

    public int MinPasswordLength { get; set; } = 12;

    public TimeSpan InviteLifetime { get; set; } = TimeSpan.FromDays(7);
}
