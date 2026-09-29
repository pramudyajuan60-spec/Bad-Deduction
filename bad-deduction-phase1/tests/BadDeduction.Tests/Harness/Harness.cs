using System.Reflection;

namespace BadDeduction.Tests.Harness;

/// <summary>
/// Minimal xunit-shaped harness so the suite runs with zero NuGet dependencies (offline CI, sandboxes).
/// Migrating to xunit later is mechanical: FactAttribute and Assert.* mirror xunit's names.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute { }

public sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message) { }
}

public static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition) throw new AssertionException(message ?? "Expected condition to be true.");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition) throw new AssertionException(message ?? "Expected condition to be false.");
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{message ?? "Values differ."}\n    expected: {expected}\n    actual:   {actual}");
    }

    public static void NotEqual<T>(T notExpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
            throw new AssertionException($"{message ?? "Values should differ."}\n    both:     {actual}");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? message = null)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a))
            throw new AssertionException($"{message ?? "Sequences differ."}\n    expected: [{string.Join(", ", e)}]\n    actual:   [{string.Join(", ", a)}]");
    }

    public static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
            throw new AssertionException($"Expected text to contain '{expectedSubstring}'.\n    actual: {actual}");
    }

    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        catch (Exception ex) { throw new AssertionException($"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertionException($"Expected {typeof(T).Name} but nothing was thrown.");
    }
}

public static class Runner
{
    public static int Run(Assembly assembly, string? filter)
    {
        var tests = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
                .Select(m => (Type: t, Method: m)))
            .Where(x => filter is null || $"{x.Type.Name}.{x.Method.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Type.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Method.Name, StringComparer.Ordinal)
            .ToList();

        int passed = 0, failed = 0;
        foreach (var (type, method) in tests)
        {
            var name = $"{type.Name}.{method.Name}";
            try
            {
                var instance = Activator.CreateInstance(type);
                method.Invoke(instance, null);
                passed++;
                Console.WriteLine($"  PASS  {name}");
            }
            catch (Exception ex)
            {
                failed++;
                var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
                Console.WriteLine($"  FAIL  {name}\n        {inner.Message.Replace("\n", "\n        ")}");
                if (inner is not AssertionException) Console.WriteLine("        " + inner.StackTrace?.Split('\n').FirstOrDefault()?.Trim());
            }
        }

        Console.WriteLine($"\n{passed} passed, {failed} failed, {tests.Count} total");
        return failed == 0 && tests.Count > 0 ? 0 : 1;
    }
}
