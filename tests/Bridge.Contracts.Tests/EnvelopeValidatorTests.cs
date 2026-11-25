using Bridge.Contracts;
using Xunit;

namespace Bridge.Contracts.Tests;

/// <summary>
/// C# mirror of the contract_lab protocol cases that are about the wire
/// format (not the FakeDesktop behaviour). Golden vectors are shared with
/// schemas/command.example.json: same field order/semantics, fresh times.
/// </summary>
public sealed class EnvelopeValidatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static string ValidJson(string operation = "input.click", string payload = """{"x": 320, "y": 180}""") =>
        $$"""
        {"schema_version": "1.0",
         "command_id": "11d497af-51d1-48ec-a5ab-c878e3cd6b4e",
         "device_id": "test-device",
         "lease_id": "523fe7e5-6ba0-4d6b-a662-e2cebe25f687",
         "epoch": 3, "sequence": 1,
         "operation": "{{operation}}",
         "observation_id": "81c2158c-0af9-4e4d-aa04-f7a742989f59",
         "issued_at": "2026-11-13T12:00:00Z",
         "expires_at": "2026-11-13T12:00:15Z",
         "payload": {{payload}}}
        """;

    private static EnvelopeException AssertRejects(string json) =>
        Assert.Throws<EnvelopeException>(() => EnvelopeValidator.Validate(json, Now));

    [Fact]
    public void ValidClickEnvelopePasses()
    {
        var env = EnvelopeValidator.Validate(ValidJson(), Now);
        Assert.Equal("input.click", env.Operation);
        Assert.Equal(new ClickPayload(320, 180), env.Payload);
        Assert.Equal(3, env.Epoch);
    }

    [Fact]
    public void UnknownEnvelopeFieldRejected() =>
        AssertRejects(ValidJson().Replace("\"payload\"", "\"button\": \"left\", \"payload\""));

    [Fact]
    public void DuplicateKeyRejected() =>
        AssertRejects(ValidJson().Replace("\"epoch\": 3", "\"epoch\": 3, \"epoch\": 3"));

    [Fact]
    public void UppercaseUuidRejected() =>
        AssertRejects(ValidJson().Replace("11d497af", "11D497AF"));

    [Fact]
    public void TtlOver30sRejected() =>
        AssertRejects(ValidJson().Replace("12:00:15Z", "12:00:31Z"));

    [Fact]
    public void ExpiredEnvelopeRejected() =>
        AssertRejects(ValidJson().Replace("12:00:00Z", "11:59:00Z").Replace("12:00:15Z", "11:59:15Z"));

    [Fact]
    public void IssuedTooFarAheadRejected() =>
        AssertRejects(ValidJson().Replace("12:00:00Z", "12:00:03Z").Replace("12:00:15Z", "12:00:18Z"));

    [Fact]
    public void UnknownOperationRejected() =>
        AssertRejects(ValidJson("input.right_click"));

    [Fact]
    public void NegativeClickRejected() =>
        AssertRejects(ValidJson(payload: """{"x": -1, "y": 5}"""));

    [Fact]
    public void ClickExtraPayloadFieldRejected() =>
        AssertRejects(ValidJson(payload: """{"x": 1, "y": 2, "button": "left"}"""));

    [Fact]
    public void TypeTextAcceptsChineseAndEmoji()
    {
        var env = EnvelopeValidator.Validate(
            ValidJson("input.type_text", """{"text": "你好，Windows 👋"}"""), Now);
        Assert.Equal(new TypeTextPayload("你好，Windows 👋"), env.Payload);
    }

    [Fact]
    public void TypeTextRejectsNul() =>
        AssertRejects(ValidJson("input.type_text", """{"text": "a\u0000b"}"""));

    [Fact]
    public void TypeTextRejectsTooLong() =>
        AssertRejects(ValidJson("input.type_text",
            "{\"text\": \"" + new string('a', 2001) + "\"}"));

    [Fact]
    public void KeyCombinationAcceptsCtrlC()
    {
        var env = EnvelopeValidator.Validate(
            ValidJson("input.key", """{"keys": ["CTRL", "C"]}"""), Now);
        // Compare the key sequence, not the container type (validator
        // returns List<string>; the literal here is string[]).
        Assert.Equal(new[] { "CTRL", "C" }, ((KeyPayload)env.Payload).Keys);
    }

    [Fact]
    public void KeyRejectsUnknownKey() =>
        AssertRejects(ValidJson("input.key", """{"keys": ["SUPER"]}"""));

    [Fact]
    public void KeyRejectsDuplicate() =>
        AssertRejects(ValidJson("input.key", """{"keys": ["CTRL", "CTRL"]}"""));

    [Fact]
    public void KeyRejectsTooMany() =>
        AssertRejects(ValidJson("input.key",
            """{"keys": ["CTRL", "ALT", "SHIFT", "A", "B"]}"""));

    [Fact]
    public void CoordinateMapUsesFloorAndNegativeOrigin()
    {
        // image 1024x768, crop at (-1920, 0) size 1920x1080: point (512,384)
        // -> -1920 + 512*1920/1024 = -960 ; 0 + 384*1080/768 = 540
        var (x, y) = CoordinateMapper.Map(512, 384, 1024, 768, -1920, 0, 1920, 1080);
        Assert.Equal(-960, x);
        Assert.Equal(540, y);
    }

    [Fact]
    public void CoordinateMapRejectsOutsideImage() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CoordinateMapper.Map(1024, 0, 1024, 768, 0, 0, 1920, 1080));
}
