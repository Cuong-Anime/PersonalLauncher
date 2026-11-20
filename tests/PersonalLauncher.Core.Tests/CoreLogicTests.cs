using PersonalLauncher.Core.Services;
using Xunit;

namespace PersonalLauncher.Core.Tests;

public sealed class CoreLogicTests
{
    [Theory]
    [InlineData("Đấu Trường Chân Lý", "dau truong chan ly")]
    [InlineData("  Thế   Giới   Ảo  ", "the gioi ao")]
    [InlineData("Đường Đua Tốc Độ", "duong dua toc do")]
    [InlineData("Genshin Impact", "genshin impact")]
    [InlineData("原神 冒険", "原神 冒険")]
    [InlineData("崩坏：星穹铁道", "崩坏：星穹铁道")]
    public void NormalizeForSearch_HandlesVietnameseJapaneseChinese(string input, string expected)
    {
        string actual = TextNormalization.NormalizeForSearch(input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(@"D:\Games\MyGame\game.exe", "MyGame")]
    [InlineData(@"D:\Games\MyGame\Binaries\Win64\game.exe", "Win64")]
    [InlineData(@"C:\Program Files\Tên Game Có Dấu\launch.exe", "Tên Game Có Dấu")]
    public void GetDirectParentFolderName_AlwaysUsesImmediateParent(string exePath, string expected)
    {
        Assert.Equal(expected, PathNormalization.GetDirectParentFolderName(exePath));
    }

    [Fact]
    public void NormalizeExePathKey_IsCaseInsensitiveAndCanonical()
    {
        string a = PathNormalization.NormalizeExePathKey(@"D:\Games\Sub\..\MyGame\Game.EXE");
        string b = PathNormalization.NormalizeExePathKey(@"d:\games\mygame\game.exe");
        Assert.Equal(a, b);
    }

    [Fact]
    public void IsPathInsideDirectory_PreventsTraversalEscape()
    {
        Assert.True(PathNormalization.IsPathInsideDirectory(@"C:\Data\Assets\games\abc\icon.png", @"C:\Data\Assets\games"));
        Assert.False(PathNormalization.IsPathInsideDirectory(@"C:\Data\Assets\games\..\secret.txt", @"C:\Data\Assets\games"));
        Assert.False(PathNormalization.IsPathInsideDirectory(@"C:\Data\Assets\games2\abc\icon.png", @"C:\Data\Assets\games"));
    }

    [Theory]
    [InlineData("0042", true)]
    [InlineData("0000", true)]
    [InlineData("9876", true)]
    [InlineData("42", false)]
    [InlineData("00421", false)]
    [InlineData("00a2", false)]
    [InlineData("", false)]
    [InlineData(" 042", false)]
    public void PinValidation_PreservesLeadingZerosAndRejectsInvalid(string pin, bool expected)
    {
        Assert.Equal(expected, PinValidation.IsValidPin(pin));
    }

    [Fact]
    public void ScanHeuristics_FlagsHelpersAndPrioritizesMatchingGameExe()
    {
        Assert.True(ScanHeuristics.ClassifyExecutable(@"D:\Games\Foo\unins000.exe").IsHelper);
        Assert.True(ScanHeuristics.ClassifyExecutable(@"D:\Games\Foo\UnityCrashHandler64.exe").IsHelper);
        Assert.True(ScanHeuristics.ClassifyExecutable(@"D:\Games\Foo\_CommonRedist\vcredist_x64.exe").IsHelper);
        Assert.False(ScanHeuristics.ClassifyExecutable(@"D:\Games\StarGame\StarGame.exe").IsHelper);

        int mainScore = ScanHeuristics.ScorePrimaryCandidate(@"D:\Games\StarGame\StarGame.exe", "StarGame");
        int cfgScore = ScanHeuristics.ScorePrimaryCandidate(@"D:\Games\StarGame\ConfigTool.exe", "StarGame");
        Assert.True(mainScore > cfgScore);
    }

    [Fact]
    public void MailRetryPolicy_FollowsExponentialScheduleWithJitterBounds()
    {
        TimeSpan r0Min = MailRetryPolicy.GetNextDelay(0, 0.0);
        TimeSpan r0Max = MailRetryPolicy.GetNextDelay(0, 1.0);
        Assert.InRange(r0Min.TotalSeconds, 50, 52);
        Assert.InRange(r0Max.TotalSeconds, 68, 70);

        TimeSpan r1Mid = MailRetryPolicy.GetNextDelay(1, 0.5);
        Assert.InRange(r1Mid.TotalMinutes, 4.9, 5.1);

        TimeSpan r2Mid = MailRetryPolicy.GetNextDelay(2, 0.5);
        Assert.InRange(r2Mid.TotalMinutes, 14.9, 15.1);

        TimeSpan r3Mid = MailRetryPolicy.GetNextDelay(3, 0.5);
        Assert.InRange(r3Mid.TotalMinutes, 59.9, 60.1);

        // Caps at 1 hour for higher retry counts
        TimeSpan r8Mid = MailRetryPolicy.GetNextDelay(8, 0.5);
        Assert.InRange(r8Mid.TotalMinutes, 59.9, 60.1);
    }
}
