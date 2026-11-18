using System.Collections.Frozen;
using System.Text;

namespace Auction.Engine;

/// <summary>Domain enums as the snake_case text the database stores and checks ('minimum_bid', 'reserve_not_met').</summary>
internal static class DbNames
{
    public static string Of<T>(T value) where T : struct, Enum => Names<T>.ToDb[value];

    public static T Parse<T>(string text) where T : struct, Enum => Names<T>.FromDb[text];

    private static class Names<T> where T : struct, Enum
    {
        public static readonly FrozenDictionary<T, string> ToDb =
            Enum.GetValues<T>().ToFrozenDictionary(v => v, v => SnakeCase(v.ToString()));

        public static readonly FrozenDictionary<string, T> FromDb =
            ToDb.ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

        private static string SnakeCase(string name)
        {
            var result = new StringBuilder(name.Length + 4);
            foreach (var c in name)
            {
                if (char.IsUpper(c) && result.Length > 0)
                {
                    result.Append('_');
                }

                result.Append(char.ToLowerInvariant(c));
            }

            return result.ToString();
        }
    }
}
