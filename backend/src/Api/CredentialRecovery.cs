using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

public static class CredentialRecovery
{
    // Local operator access only; no HTTP reset endpoint or password command-line argument.
    public static async Task ResetAsync(MediaDb db, string password)
    {
        if (password.Length is < 12 or > 256)
            throw new ArgumentException("Password must contain 12–256 characters.");
        var admin =
            await db.Users.SingleOrDefaultAsync(x => x.Id == 1 && x.Role == "Admin")
            ?? throw new InvalidOperationException(
                "No configured administrator exists in this data directory."
            );
        var hash = await Passwords.Hash(password);
        await using var transaction = await db.Database.BeginTransactionAsync();
        admin.PasswordHash = hash;
        await db.SaveChangesAsync();
        await db.Sessions.Where(x => x.UserId == admin.Id).ExecuteDeleteAsync();
        await transaction.CommitAsync();
    }

    public static string ReadPassword()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return password.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                    password.Length--;
            }
            else if (!char.IsControl(key.KeyChar) && password.Length < 257)
                password.Append(key.KeyChar);
        }
    }
}
