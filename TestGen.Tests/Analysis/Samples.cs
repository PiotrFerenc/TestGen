namespace TestGen.Tests.Analysis;

internal static class Samples
{
    public const string Calc = """
        using System;

        namespace Acme.Core.Services;

        public interface IClock { DateTime Now { get; } }

        public abstract class Notifier
        {
            public abstract void Notify(string msg);
            public void Log() { }
        }

        public class Calc
        {
            private readonly IClock _clock;

            public Calc(IClock clock, Notifier notifier, int seed) { _clock = clock; }

            public int Add(int a, int b) => a + b;

            public string Add(string a, int b) { return a + b; }

            public int Run(int x) => Helper(x);

            private int Helper(int x) => Deep(x) + 1;

            private int Deep(int x) => x * 2;

            public DateTime Now { get { return _clock.Now; } }

            public class Inner { public void Go() { } }
        }

        public class Box<T> { public T? Value { get; set; } }
        """;

    public static readonly (string, string)[] Prod = [("Services/Calc.cs", Calc)];

    public const string XunitTests = """
        using System;
        using Acme.Core.Services;
        using NSubstitute;
        using Shouldly;
        using Xunit;

        namespace Acme.Core.Tests.Services;

        public class CalcTests
        {
            private readonly IClock _clock = Substitute.For<IClock>();

            public CalcTests()
            {
            }

            [Fact]
            public void Add_TwoNumbers_ReturnsSum()
            {
                // Arrange
                var calc = new Calc(_clock, null!, 1);
                // Act
                var result = calc.Add(1, 2);
                // Assert
                result.ShouldBe(3);
            }

            [Theory]
            [InlineData(1)]
            public void Run_Positive_ReturnsDoublePlusOne(int x)
            {
                // Arrange
                var calc = new Calc(_clock, null!, 1);
                // Act & Assert
                calc.Run(x).ShouldBe(x * 2 + 1);
            }
        }
        """;

    public const string NunitTests = """
        using FluentAssertions;
        using Moq;
        using NUnit.Framework;
        using System;

        namespace Acme.Core.Tests
        {
            [TestFixture]
            public class CalcTests
            {
                private Mock<IClock> _clock;

                [SetUp]
                public void Init() { _clock = new Mock<IClock>(); }

                [Test]
                public void Add_Should_ReturnSum()
                {
                    new Calc(_clock.Object, null, 1).Add(1, 2).Should().Be(3);
                }

                [Test]
                public void Run_Should_Double()
                {
                    new Calc(_clock.Object, null, 1).Run(2).Should().Be(5);
                }
            }
        }
        """;
}
