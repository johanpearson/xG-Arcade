using System.Security.Cryptography;

namespace XGArcade.Core.Leagues;

// REQ-402: a short, shareable invite code — 6 characters drawn from a
// 31-symbol alphabet (~887 million possible codes), deliberately excluding
// visually-ambiguous characters (0/O, 1/I, L) so a code read aloud,
// handwritten, or texted between friends is never misheard or mistyped —
// the same shareability concern behind e.g. an OTP/confirmation code.
// Collision-resistance at this codebase's scale comes from the
// alphabet/length, not from RandomNumberGenerator specifically being
// cryptographically secure — it's used here simply because it's already
// available and removes any doubt about distribution.
//
// NOTE for manual review: this is a new design decision (invite code
// format/generation), not something copied from an existing convention in
// this codebase — flagged for human review per this story's design notes.
public class InviteCodeGenerator : IInviteCodeGenerator
{
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    private const int Length = 6;

    public string Generate()
    {
        Span<char> buffer = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
        {
            buffer[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(buffer);
    }
}
