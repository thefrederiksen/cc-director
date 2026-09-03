namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// SERIALISES THE ONE TEST THAT MUTATES THE PROCESS-GLOBAL DATABASE CONFIGURATION AGAINST EVERY TEST THAT
/// OPENS A DATABASE.
///
/// <c>GatewayDatabase</c> reads its provider selection from the environment variable
/// <c>CC_GATEWAY_DB_CONNECTION</c>, and one test has to set that variable to a BLANK value to prove that a
/// blank connection string still fails in the constructor rather than being waited out. The variable is
/// process-global, the suite runs in parallel, and a database opened by any other test inside that window
/// fails with "CC_GATEWAY_DB_CONNECTION is set but blank" - a message about a fault in the test that
/// happens to be running, not about the test that failed.
///
/// This is not hypothetical and it is not new: the comment on the template in
/// <see cref="GatewayDbTestHarness"/> records the same SHAPE of defect from a process-global connection
/// pool clear, where "every full run failed exactly one database test, a different one each time, all
/// passing in isolation". That is the signature to recognise. It reappeared when this round added tests
/// that open a database, which did not create the hazard but did make it land.
///
/// So the two are serialised, and asymmetrically, because that is what the hazard is: MANY tests open
/// databases and may do so together, and exactly ONE mutates the variable and must do it alone.
/// </summary>
internal static class GatewayDbEnvironmentGate
{
    private static readonly ReaderWriterLockSlim Gate = new(LockRecursionPolicy.SupportsRecursion);

    /// <summary>
    /// Open a database while the configuration is guaranteed to be the one the suite runs with. Many
    /// callers may hold this at once - opening a database is not the hazard.
    /// </summary>
    internal static T WhileTheConfigurationIsStable<T>(Func<T> open)
    {
        Gate.EnterReadLock();
        try { return open(); }
        finally { Gate.ExitReadLock(); }
    }

    /// <summary>
    /// Change the process-global configuration, with no database being opened anywhere. Exactly one test
    /// needs this, and it needs to be alone.
    /// </summary>
    internal static void WhileNobodyIsOpeningADatabase(Action mutate)
    {
        Gate.EnterWriteLock();
        try { mutate(); }
        finally { Gate.ExitWriteLock(); }
    }
}
