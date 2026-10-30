using Xunit;

namespace Gpp.Tests;

// The one question these pin: is a lone number a target or an amount? It is a target, always, because
// .players is what people read numbers off. Two call sites a month apart would answer it differently.
public class ToyArgsTests
{
    private static (string Needle, int? Amount, int? Amount2) Split(string arg)
    {
        var parsed = ToyArgs.Split(arg);
        return (parsed.Needle, parsed.Amount, parsed.Amount2);
    }

    [Fact]
    public void NothingIsYourself() => Assert.Equal(("", null, null), Split(""));

    [Fact]
    public void ALoneNumberIsATarget() => Assert.Equal(("5", null, null), Split("5"));

    [Fact]
    public void ANumberAfterATargetIsTheAmount() => Assert.Equal(("5", 2, null), Split("5 2"));

    [Fact]
    public void AThirdNumberIsTheSecondAmount() => Assert.Equal(("5", 2, 3), Split("5 2 3"));

    [Fact]
    public void MeTakesBothAmounts() => Assert.Equal(("me", 5, 2), Split("me 5 2"));

    [Fact]
    public void ANameKeepsItsSpaces() => Assert.Equal(("Tani Rani", 20, null), Split("Tani Rani 20"));

    // Only two are taken, so a fourth number stays part of the name rather than being silently dropped.
    [Fact]
    public void OnlyTwoAmountsAreTaken() => Assert.Equal(("1 2", 3, 4), Split("1 2 3 4"));

    [Fact]
    public void ANameEndingInADigitIsStillFound() => Assert.Equal(("elem3nt", null, null), Split("elem3nt"));

    [Theory]
    [InlineData("")]
    [InlineData("me")]
    [InlineData("self")]
    [InlineData("myself")]
    public void TheWordsThatMeanYou(string needle) => Assert.True(ToyArgs.MeansYourself(needle));

    [Theory]
    [InlineData("4")]
    [InlineData("Boot")]
    [InlineData("all")]
    public void AndTheOnesThatDoNot(string needle) => Assert.False(ToyArgs.MeansYourself(needle));
}
