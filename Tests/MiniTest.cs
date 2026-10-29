using System.Reflection;

namespace MapExpansions.Tests
{
    internal sealed class AssertException : Exception
    {
        public AssertException(string m) : base(m) { }
    }

    /// <summary>Tiny zero-dependency assert helpers (throw on failure).</summary>
    internal static class Check
    {
        public static void True(bool c, string msg = "") { if (!c) throw new AssertException("expected true. " + msg); }

        public static void False(bool c, string msg = "") { if (c) throw new AssertException("expected false. " + msg); }

        public static void Eq(object expected, object actual, string msg = "")
        {
            if (!Equals(expected, actual)) throw new AssertException($"expected [{expected}] but got [{actual}]. {msg}");
        }

        public static void Contains(string haystack, string needle, string msg = "")
        {
            if (haystack == null || !haystack.Contains(needle))
                throw new AssertException($"expected [{haystack}] to contain [{needle}]. {msg}");
        }

        public static void NotContains(string haystack, string needle, string msg = "")
        {
            if (haystack != null && haystack.Contains(needle))
                throw new AssertException($"expected [{haystack}] NOT to contain [{needle}]. {msg}");
        }

        public static void Fail(string msg) => throw new AssertException(msg);
    }

    internal static class Program
    {
        /// <summary>
        /// Runs every parameterless static void method of every type whose name ends in "Tests". New test classes are
        /// picked up automatically, so a milestone can add cases without touching the runner.
        /// </summary>
        private static int Main()
        {
            List<Type> classes = Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(t => t.IsClass && t.Name.EndsWith("Tests", StringComparison.Ordinal))
                .OrderBy(t => t.Name)
                .ToList();

            int pass = 0, fail = 0;
            int total = classes.Sum(c => Cases(c).Count);
            Console.WriteLine($"MapExpansions tests - {total} cases in {classes.Count} classes\n");

            foreach (Type c in classes)
            {
                Console.WriteLine($"  {c.Name}");
                foreach (MethodInfo m in Cases(c))
                {
                    try
                    {
                        m.Invoke(null, null);
                        Console.WriteLine($"    PASS  {m.Name}");
                        pass++;
                    }
                    catch (TargetInvocationException tie)
                    {
                        Console.WriteLine($"    FAIL  {m.Name}: {tie.InnerException?.Message}");
                        fail++;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"    FAIL  {m.Name}: {e.Message}");
                        fail++;
                    }
                }
            }

            Console.WriteLine($"\n{pass} passed, {fail} failed.");
            return fail == 0 ? 0 : 1;
        }

        private static List<MethodInfo> Cases(Type t) => t
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetParameters().Length == 0 && m.ReturnType == typeof(void))
            .OrderBy(m => m.Name)
            .ToList();
    }
}
