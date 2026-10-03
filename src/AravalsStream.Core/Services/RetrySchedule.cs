namespace AravalsStream.Core.Services;
public static class RetrySchedule { private static readonly TimeSpan[] Delays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)]; public static TimeSpan GetDelay(int attempt) => Delays[Math.Min(Math.Max(attempt, 0), Delays.Length - 1)]; }

