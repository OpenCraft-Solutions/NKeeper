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
            var e = "http://example.com/ssh"; // "//" inside a string is not a comment
            var f = "esc\"aped";
            """";

        var literals = Sources.CSharpLiterals(source).ToList();

        Assert.Equal(["telnet", "web{x}", "vnc \"quoted\"", "http://example.com/ssh", "esc\"aped"], literals);
    }

    [Fact]
    public void Csharp_raw_string_literals_are_found()
    {
        var source = "var a = \"\"\"\n    kvm\n    \"\"\";";

        Assert.Equal("kvm", Assert.Single(Sources.CSharpLiterals(source)).Trim());
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
    public void Relative_imports_of_every_form_are_found()
    {
        const string source = """
            import a from './a'
            import { b } from "../b"
            import './side-effect.css'
            export * from '../../c'
            const d = await import('./d')
            const e = require("../e")
            import f from 'react'
            const g = './not-an-import'
            // import h from '../commented'
            """;

        Assert.Equal(["./a", "../b", "./side-effect.css", "../../c", "./d", "../e"], Sources.RelativeImports(source).ToList());
    }
}
