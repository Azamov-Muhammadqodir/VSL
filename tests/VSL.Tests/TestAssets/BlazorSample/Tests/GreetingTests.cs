using Components;
using Xunit;

namespace Tests;

public class GreetingTests
{
    [Fact]
    public void Default_name_is_world() => Assert.Equal("world", new Greeting().Name);

    [Theory]
    [InlineData(1, 2, 3)]
    [InlineData(2, 2, 4)]
    public void Adds(int a, int b, int sum) => Assert.Equal(sum, a + b);

    [Fact]
    public void Deliberately_failing() => Assert.Equal(1, 2);

    [Fact(Skip = "Not ready yet")]
    public void Skipped()
    {
    }
}
