using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrchardCore.Environment.Shell;
using StackExchange.Redis;

namespace OrchardCore.Distributed.Redis.Services
{
    /// <summary>
    /// This component is a tenant singleton which allows to acquire named locks for a given tenant.
    /// This is a distributed version where locks are auto released after provided expiration times.
    /// </summary>
    public class RedisLock : ILock
    {
        private readonly string _hostName;
        private readonly string _prefix;
        private readonly IRedisClient _redis;

        public RedisLock(ShellSettings shellSettings, IRedisClient redis, ILogger<RedisLock> logger)
        {
            _hostName = Dns.GetHostName() + ':' + Process.GetCurrentProcess().Id;
            _prefix = shellSettings.Name + ':';
            _redis = redis;
            Logger = logger;

            var test = new System.Collections.Generic.List<double>();
            for (int i = 1; i <= 20; i++)
            {
                var delay = GetDelay(i);
                test.Add(delay.TotalMilliseconds);
            }
        }

        public ILogger Logger { get; set; }

        /// <summary>
        /// Waits indefinitely until acquiring a named lock with a given expiration for the current tenant.
        /// After 'expiration' the lock is auto released, a null value is equivalent to 'TimeSpan.MaxValue'.
        /// </summary>
        public async Task<IDisposable> AcquireLockAsync(string key, TimeSpan? expiration = null)
        {
            return (await TryAcquireLockAsync(key, TimeSpan.MaxValue, expiration)).locker;
        }

        /// <summary>
        /// Tries to acquire a named lock in a given timeout with a given expiration for the current tenant.
        /// After 'expiration' the lock is auto released, a null value is equivalent to 'TimeSpan.MaxValue'.
        /// </summary>
        public async Task<(IDisposable locker, bool locked)> TryAcquireLockAsync(string key, TimeSpan timeout, TimeSpan? expiration = null)
        {
            using (var cts = new CancellationTokenSource(timeout))
            {
                var retries = 0;

                while (_redis.IsConnected && !cts.IsCancellationRequested)
                {
                    var locked = await LockAsync(key, expiration ?? TimeSpan.MaxValue);

                    if (locked)
                    {
                        return (new Locker(this, key), locked);
                    }

                    await Task.Delay(GetDelay(retries++), cts.Token);
                }
            }

            return (null, false);
        }

        private async Task<bool> LockAsync(string key, TimeSpan expiry)
        {
            await _redis.ConnectAsync();

            if (_redis.IsConnected)
            {
                try
                {
                    return await _redis.Database.LockTakeAsync(_prefix + key, _hostName, expiry);
                }

                catch (Exception e)
                {
                    Logger.LogError(e, "'Fails to acquire the named lock {LockName}'.", _prefix + key);
                }
            }

            return false;
        }

        public void Release(string key)
        {
            if (_redis.IsConnected)
            {
                try
                {
                    _redis.Database.LockRelease(_prefix + key, _hostName, CommandFlags.FireAndForget);
                }

                catch (Exception e)
                {
                    Logger.LogError(e, "'Fails to release the named lock {LockName}'.", _prefix + key);
                }
            }
        }

        private class Locker : IDisposable
        {
            private readonly RedisLock _lock;
            private readonly string _key;

            public Locker(RedisLock lock_, string key)
            {
                _lock = lock_;
                _key = key;
            }

            public void Dispose()
            {
                _lock.Release(_key);
            }
        }

        private static readonly double _baseMilliseconds = 1000;
        private static readonly double _maxMilliseconds = 30000;

        protected internal virtual TimeSpan GetDelay(int retries)
        {
            var milliseconds = _baseMilliseconds * (1 + ((Math.Pow(1.5, retries - 1) - 1)
                * (.7 + new Random().NextDouble() * .3)));

            return TimeSpan.FromMilliseconds(Math.Min(milliseconds, _maxMilliseconds));

            // test 1    test 2
            // ------    ------
            // 1000      1000
            // 1612      1585
            // 2529      2259
            // 3815      3451
            // 5503      5616
            // 8882      8942
            // 14390     14507
            // 18553     18125
            // 30000     27540
            // 30000     30000
        }
    }
}