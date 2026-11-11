using NKeeper.Core.Connectivity.Guacamole;

namespace NKeeper.Server.Tests;

// docs/architecture.md §7.20: the Guacamole protocol and the browser instruction allow-list, ported from 1.x with their tests.

public sealed class GuacamoleProtocolTests
{
    [Fact]
    public void Encode_CountsUnicodeCodePoints()
    {
        Assert.Equal("4.size,4.1024,3.768;", GuacamoleInstruction.Encode("size", "1024", "768"));
        Assert.Equal("4.blob,1.1,2.é😀;", GuacamoleInstruction.Encode("blob", "1", "é😀"));
        Assert.Equal("0.,6.closed;", GuacamoleInstruction.Encode(string.Empty, "closed"));
    }

    [Fact]
    public void Parser_ReassemblesFragmentsAndKeepsRawText()
    {
        var parser = new GuacamoleParser(1024);
        parser.Append("4.si");
        Assert.False(parser.TryRead(out _));

        parser.Append("ze,4.1024,3.768;5.mouse,1.1,1.2;3.ke");

        Assert.True(parser.TryRead(out var size));
        Assert.Equal("size", size.Opcode);
        Assert.Equal(["1024", "768"], size.Args);
        Assert.Equal("4.size,4.1024,3.768;", size.Raw);
        Assert.True(parser.TryRead(out var mouse));
        Assert.Equal("mouse", mouse.Opcode);
        Assert.False(parser.TryRead(out _));
    }

    [Fact]
    public void Parser_HandlesSurrogatePairsAndSeparatorsInsideValues()
    {
        var parser = new GuacamoleParser(1024);
        parser.Append("9.clipboard,1.1,10.text/plain;4.blob,1.1,3.a;😀;");

        Assert.True(parser.TryRead(out var clipboard));
        Assert.Equal(["1", "text/plain"], clipboard.Args);
        Assert.True(parser.TryRead(out var blob));
        Assert.Equal("a;😀", blob.Args[1]);
    }

    [Theory]
    [InlineData("x.size;")]
    [InlineData("4.size:")]
    [InlineData("1234567890.a;")]
    public void Parser_RejectsMalformedInput(string input)
    {
        var parser = new GuacamoleParser(1024);
        parser.Append(input);

        Assert.Throws<FormatException>(() => parser.TryRead(out _));
    }

    [Fact]
    public void Parser_RejectsOversizedInstructions()
    {
        var parser = new GuacamoleParser(16);
        parser.Append("100." + new string('a', 40));

        Assert.Throws<FormatException>(() => parser.TryRead(out _));
    }
}

public sealed class GuacamoleInstructionFilterTests
{
    private static GuacamoleInstruction Parse(string raw)
    {
        var parser = new GuacamoleParser(1 << 20);
        parser.Append(raw);
        Assert.True(parser.TryRead(out var instruction));
        return instruction;
    }

    [Theory]
    [InlineData("3.key,5.65307,1.1;")]
    [InlineData("5.mouse,3.100,3.200,1.1;")]
    [InlineData("4.size,4.1920,4.1080;")]
    [InlineData("4.sync,8.12345678;")]
    [InlineData("10.disconnect;")]
    public void InputAndDisplayInstructions_PassThrough(string raw) =>
        Assert.True(new GuacamoleInstructionFilter().IsAllowed(Parse(raw)));

    [Theory]
    [InlineData("4.file,1.1,24.application/octet-stream,8.evil.exe;")]
    [InlineData("4.pipe,1.1,10.text/plain,4.name;")]
    [InlineData("5.audio,1.1,31.audio/L16;rate=44100,channels=1;")]
    [InlineData("4.argv,1.1,10.text/plain,8.password;")]
    [InlineData("6.select,3.vnc;")]
    [InlineData("7.connect,4.host;")]
    [InlineData("3.put,1.1,1.2,10.text/plain,4.name;")]
    public void FileAudioAndHandshakeInstructions_AreDropped(string raw) =>
        Assert.False(new GuacamoleInstructionFilter().IsAllowed(Parse(raw)));

    [Fact]
    public void ClipboardStreams_OnlyTextAndOnlyForOpenedStreams()
    {
        var filter = new GuacamoleInstructionFilter();

        Assert.False(filter.IsAllowed(Parse("4.blob,1.7,4.aGk=;")));
        Assert.False(filter.IsAllowed(Parse("9.clipboard,1.7,9.image/png;")));
        Assert.True(filter.IsAllowed(Parse("9.clipboard,1.7,10.text/plain;")));
        Assert.True(filter.IsAllowed(Parse("4.blob,1.7,4.aGk=;")));
        Assert.True(filter.IsAllowed(Parse("3.end,1.7;")));
        Assert.False(filter.IsAllowed(Parse("4.blob,1.7,4.aGk=;")));
    }

    [Fact]
    public void ClipboardStreams_AreCapped()
    {
        var filter = new GuacamoleInstructionFilter();
        var chunk = new string('A', 600_000);
        Assert.True(filter.IsAllowed(Parse("9.clipboard,1.1,10.text/plain;")));

        Assert.True(filter.IsAllowed(Parse($"4.blob,1.1,{chunk.Length}.{chunk};")));
        Assert.False(filter.IsAllowed(Parse($"4.blob,1.1,{chunk.Length}.{chunk};")));
    }
}
