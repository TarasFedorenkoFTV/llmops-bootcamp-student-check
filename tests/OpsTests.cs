using Xunit;

public class MaskPiiTests
{
    [Theory]
    [InlineData("мій email taras@example.com, скиньте пароль",
                "мій email [email], скиньте пароль")]
    [InlineData("two: a.b-c@x.io and second_user@corp.example.org!",
                "two: [email] and [email]!")]
    [InlineData("без пошти нічого не маскуємо", "без пошти нічого не маскуємо")]
    public void Masks_emails_before_model_and_cache(string input, string expected)
        => Assert.Equal(expected, Ops.MaskPii(input));

    [Fact]
    public void Masked_output_is_idempotent() // guard має бути ідемпотентним до власного виходу (L08)
        => Assert.Equal("[email]", Ops.MaskPii(Ops.MaskPii("x@y.com")));
}

public class RouteTests
{
    [Theory]
    [InlineData("Як скинути пароль?", "mock-mini")]
    [InlineData("хочу повернути гроші", "mock-strong")]
    [InlineData("ТЕРМІНОВО, не працює!", "mock-strong")]
    [InlineData("i want a refund", "mock-strong")]
    [InlineData("маю скаргу на оператора", "mock-strong")]
    [InlineData("дякую, але хочу повернути кошти", "mock-strong")] // конфлікт: ескалація перемагає
    public void Routes_by_escalation_markers(string message, string expectedModel)
        => Assert.Equal(expectedModel, Ops.Route(message, "mock"));

    [Fact]
    public void Real_key_bypasses_router()
        => Assert.Equal("gpt-4o-mini", Ops.Route("хочу повернути гроші", "gpt-4o-mini"));
}
