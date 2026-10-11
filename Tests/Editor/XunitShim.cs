// Lets the engine free xUnit suite in Tests/Editor/Core run, unchanged, in
// the Unity Test Runner. The same sources are compiled on .NET against the
// real xUnit by Tests~/Core; this file is not part of that project.
//
// Only the attributes and assertions the suite uses are mapped. Each
// assertion keeps xUnit's meaning (exact exception type for ThrowsAsync,
// element by element comparison for sequences) and fails through NUnit.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Xunit
{
    /// <summary>xUnit's [Fact], as an NUnit test.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public class FactAttribute : NUnit.Framework.TestAttribute
    {
    }

    /// <summary>xUnit's [Theory]. The cases come from [InlineData].</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public class TheoryAttribute : Attribute
    {
    }

    /// <summary>xUnit's [InlineData], as an NUnit test case.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public class InlineDataAttribute : NUnit.Framework.TestCaseAttribute
    {
        public InlineDataAttribute(params object[] data) : base(data ?? new object[] { null })
        {
        }
    }

    public static class Assert
    {
        private static void Fail(string message)
        {
            NUnit.Framework.Assert.Fail(message);
        }

        private static string Show(object value)
        {
            if (value == null) return "null";
            if (value is string) return "\"" + value + "\"";
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null) return "[" + string.Join(", ", sequence.Cast<object>().Select(Show)) + "]";
            return value + " (" + value.GetType().Name + ")";
        }

        public static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                Fail("Assert.Equal failed. Expected " + Show(expected) + ", actual " + Show(actual));
            }
        }

        public static void Equal<T>(IEnumerable<T> expected, IEnumerable<T> actual)
        {
            List<T> left = expected == null ? null : expected.ToList();
            List<T> right = actual == null ? null : actual.ToList();
            bool same = left == null || right == null
                ? left == right
                : left.SequenceEqual(right, EqualityComparer<T>.Default);
            if (!same) Fail("Assert.Equal failed. Expected " + Show(left) + ", actual " + Show(right));
        }

        // Two arrays would otherwise bind to Equal<T[]>(T, T) and compare by
        // reference; xUnit compares them element by element.
        public static void Equal<T>(T[] expected, T[] actual)
        {
            Equal((IEnumerable<T>)expected, (IEnumerable<T>)actual);
        }

        public static void NotEqual<T>(T expected, T actual)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual))
            {
                Fail("Assert.NotEqual failed. Both were " + Show(actual));
            }
        }

        public static void True(bool condition)
        {
            if (!condition) Fail("Assert.True failed.");
        }

        public static void True(bool condition, string message)
        {
            if (!condition) Fail("Assert.True failed. " + message);
        }

        public static void False(bool condition)
        {
            if (condition) Fail("Assert.False failed.");
        }

        public static void False(bool condition, string message)
        {
            if (condition) Fail("Assert.False failed. " + message);
        }

        public static void Null(object value)
        {
            if (value != null) Fail("Assert.Null failed. Actual " + Show(value));
        }

        public static void NotNull(object value)
        {
            if (value == null) Fail("Assert.NotNull failed.");
        }

        public static void Empty(IEnumerable collection)
        {
            if (collection == null) Fail("Assert.Empty failed. The collection was null.");
            else if (collection.Cast<object>().Any()) Fail("Assert.Empty failed. Actual " + Show(collection));
        }

        public static T Single<T>(IEnumerable<T> collection)
        {
            List<T> items = collection == null ? new List<T>() : collection.ToList();
            if (items.Count != 1)
            {
                Fail("Assert.Single failed. Expected one item, found " + items.Count + ": " + Show(items));
            }
            return items[0];
        }

        public static void Contains<T>(T expected, IEnumerable<T> collection)
        {
            if (collection == null || !collection.Contains(expected, EqualityComparer<T>.Default))
            {
                Fail("Assert.Contains failed. " + Show(expected) + " is not in " + Show(collection));
            }
        }

        public static void Matches(string pattern, string actual)
        {
            if (actual == null || !Regex.IsMatch(actual, pattern))
            {
                Fail("Assert.Matches failed. " + Show(actual) + " does not match " + pattern);
            }
        }

        public static void All<T>(IEnumerable<T> collection, Action<T> check)
        {
            if (collection == null) Fail("Assert.All failed. The collection was null.");
            foreach (T item in collection) check(item);
        }

        public static async Task<T> ThrowsAsync<T>(Func<Task> call) where T : Exception
        {
            try
            {
                await call();
            }
            catch (Exception e)
            {
                if (e.GetType() == typeof(T)) return (T)e;
                Fail("Assert.ThrowsAsync failed. Expected " + typeof(T).Name + ", caught " + e.GetType().Name + ": " + e.Message);
                return null;
            }
            Fail("Assert.ThrowsAsync failed. Expected " + typeof(T).Name + ", nothing was thrown.");
            return null;
        }
    }
}
