internal static class SetupSequence
{
    // No persistence, activation or secret storage. Stop on the first failed stage.
    internal static async Task<(TFirst First, TSecond? Second)> Run<TFirst, TSecond>(
        Func<Task<TFirst>> save, Func<TFirst, bool> succeeded,
        Func<Task<TSecond>> connect) where TSecond : class
    {
        var first = await save();
        return (first, succeeded(first) ? await connect() : null);
    }
}
