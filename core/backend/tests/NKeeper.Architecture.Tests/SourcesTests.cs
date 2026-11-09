namespace NKeeper.Architecture.Tests;

/// <summary>The source reading the rules rely on: if it misses a literal or an import, a rule passes vacuously.</summary>
public sealed class SourcesTests
{
    [Fact]
    public void Csharp_literals_of_every_kind_are_found_and_comments_skipped()
    {
        const string source = """"
            // a comment naming "ssh" doesn't count
            /* nor "rdp" here */
            var a = "telnet";
            var b = $"web{x}";
            var c = @"vnc ""quoted""";
            var d = '"';
            var e = "http://example.com/x"; // "//" inside a string is not a comment
            var f = "esc\"aped";
            var g = """
                kvm
                """;
            var h = "ase"u8;
            """";

        var literals = Sources.CSharpLiterals(source).ToList();

        Assert.Equal(["telnet", "web", "vnc \"quoted\"", "http://example.com/x", "esc\"aped", "kvm", "ase"], literals);
    }

    [Fact]
    public void Csharp_escapes_are_decoded_and_literals_inside_interpolations_found()
    {
        const string source = """
            var a = "ssh";
            var b = "\nrdp";
            var c = $"{Lookup("telnet")} and {(x ? "vnc" : $"{"kvm"}")}";
            """;

        var literals = Sources.CSharpLiterals(source).Select(l => l.Trim()).ToList();

        Assert.Contains("ssh", literals);
        Assert.Contains("rdp", literals);
        Assert.Contains("telnet", literals);
        Assert.Contains("vnc", literals);
        Assert.Contains("kvm", literals);
    }

    [Fact]
    public void Script_literals_of_every_kind_are_found_and_comments_skipped()
    {
        const string source = """
            // 'ssh' in a comment
            /** "rdp" in a doc comment */
            const a = 'telnet'
            const b = "web"
            const c = `vnc`
            const d = 'it\'s'
            const e = "a // not a comment"
            """;

        Assert.Equal(["telnet", "web", "vnc", "it's", "a // not a comment"], Sources.ScriptLiterals(source).ToList());
    }

    [Fact]
    public void Script_escapes_are_decoded_and_literals_inside_substitutions_found()
    {
        const string source = """
            const a = 'ssh'
            const b = "\nrdp"
            const c = '\x74elnet'
            const d = `\u{76}nc`
            const e = `before ${lookup('kvm')} middle ${x ? `${"ase"}` : {}.y} after`
            """;

        var literals = Sources.ScriptLiterals(source).Select(l => l.Trim()).ToList();

        Assert.Contains("ssh", literals);
        Assert.Contains("rdp", literals);
        Assert.Contains("telnet", literals);
        Assert.Contains("vnc", literals);
        Assert.Contains("kvm", literals);
        Assert.Contains("ase", literals);
        Assert.Contains("after", literals);
    }

    [Fact]
    public void Relative_imports_of_every_form_are_found()
    {
        const string source = """
            import a from './a'
            import { b } from "../b"
            import './side-effect.css'
            export * from '../../c'
            const d = await import('./d')
            const e = require("../e")
            const f = await import(/* @vite-ignore */ '../f')
            import g from /* why */ '../g'
            const h = `${await import('../h')}`
            import i from 'react'
            const j = './not-an-import'
            // import k from '../commented'
            """;

        Assert.Equal(["./a", "../b", "./side-effect.css", "../../c", "./d", "../e", "../f", "../g", "../h"], Sources.RelativeImports(source).ToList());
    }
}
