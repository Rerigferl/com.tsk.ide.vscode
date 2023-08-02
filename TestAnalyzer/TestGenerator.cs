using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Generators
{
    [Generator]
    public class TestGenerator : ISourceGenerator
    {
        public static readonly DiagnosticDescriptor testDiagnostic = new DiagnosticDescriptor(
            "TEST001",
            "TestWarning",
            "This is a test warning from the analyzer", "Testing", DiagnosticSeverity.Warning, true);

        public void Initialize(GeneratorInitializationContext context)
        {
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (context.Compilation.AssemblyName == "Assembly-CSharp")
            {
                string sourceText = """
                using UnityEngine;

                namespace TestCode
                {
                    public class TestClass
                    {
                        public static void TestMethod()
                        {
                            Debug.Log("Hello from the generated class");
                        }
                    }
                }
                """;

                context.AddSource("Test.generated.cs", SourceText.From(sourceText, Encoding.UTF8));
            }

            context.ReportDiagnostic(Diagnostic.Create(testDiagnostic, Location.Create("Test.generated.cs", default, default)));
        }
    }
}
