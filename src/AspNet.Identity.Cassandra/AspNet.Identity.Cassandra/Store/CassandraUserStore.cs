﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AspNet.Identity.Cassandra.Entities;
using Cassandra;
using Microsoft.AspNet.Identity;

namespace AspNet.Identity.Cassandra.Store
{
    public class CassandraUserStore : IUserStore<CassandraUser, Guid>, IUserLoginStore<CassandraUser, Guid>, IUserClaimStore<CassandraUser, Guid>,
                                      IUserPasswordStore<CassandraUser, Guid>, IUserSecurityStampStore<CassandraUser, Guid>
        // IUserTwoFactorStore<TUser, string>,
        // IUserLockoutStore<TUser, string>,
        // IUserEmailStore<TUser>,
        // IUserPhoneNumberStore<TUser> 
    {
        // A cached copy of some completed task
        private static readonly Task<bool> TrueTask = Task.FromResult(true);
        private static readonly Task<bool> FalseTask = Task.FromResult(false);
        private static readonly Task CompletedTask = TrueTask;

        private readonly ISession _session;

        // Reusable prepared statements, lazy evaluated
        private readonly AsyncLazy<PreparedStatement[]> _createUser;
        private readonly AsyncLazy<PreparedStatement[]> _updateUser;
        private readonly AsyncLazy<PreparedStatement[]> _deleteUser;
        private readonly AsyncLazy<PreparedStatement> _findById;
        private readonly AsyncLazy<PreparedStatement> _findByName;

        private readonly AsyncLazy<PreparedStatement[]> _addLogin;
        private readonly AsyncLazy<PreparedStatement[]> _removeLogin;
        private readonly AsyncLazy<PreparedStatement> _getLogins;
        private readonly AsyncLazy<PreparedStatement> _getLoginsByProvider;

        private readonly AsyncLazy<PreparedStatement> _getClaims;
        private readonly AsyncLazy<PreparedStatement> _addClaim;
        private readonly AsyncLazy<PreparedStatement> _removeClaim;

        public CassandraUserStore(ISession session)
        {
            _session = session;

            // TODO:  Currently broken because no attributes on POCOs
            // _session.GetTable<CassandraUser>().CreateIfNotExists();
            // _session.GetTable<CassandraUserClaim>().CreateIfNotExists();
            // _session.GetTable<CassandraUserLogin>().CreateIfNotExists();

            // Create some reusable prepared statements so we pay the cost of preparing once, then bind multiple times
            _createUser = new AsyncLazy<PreparedStatement[]>(() => Task.WhenAll(new []
            {
                _session.PrepareAsync("INSERT INTO users (userid, username, password_hash, security_stamp) VALUES (?, ?, ?, ?)"),
                _session.PrepareAsync("INSERT INTO users_by_username (username, userid, password_hash, security_stamp) VALUES (?, ?, ?, ?)")
            }));
            _updateUser = new AsyncLazy<PreparedStatement[]>(() => Task.WhenAll(new []
            {
                _session.PrepareAsync("UPDATE users SET password_hash = ?, security_stamp = ? WHERE userid = ?"),
                _session.PrepareAsync("UPDATE users_by_username SET password_hash = ?, security_stamp = ? WHERE username = ?")
            }));
            _deleteUser = new AsyncLazy<PreparedStatement[]>(() => Task.WhenAll(new []
            {
                _session.PrepareAsync("DELETE FROM users WHERE userid = ?"),
                _session.PrepareAsync("DELETE FROM users_by_username WHERE username = ?")
            }));
            _findById = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync("SELECT * FROM users WHERE userid = ?"));
            _findByName = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync("SELECT * FROM users_by_username WHERE username = ?"));
            
            _addLogin = new AsyncLazy<PreparedStatement[]>(() => Task.WhenAll(new []
            {
                _session.PrepareAsync("INSERT INTO logins (userid, login_provider, provider_key) VALUES (?, ?, ?)"),
                _session.PrepareAsync("INSERT INTO logins_by_provider (login_provider, provider_key, userid) VALUES (?, ?, ?)")
            }));
            _removeLogin = new AsyncLazy<PreparedStatement[]>(() => Task.WhenAll(new []
            {
                _session.PrepareAsync("DELETE FROM logins WHERE userId = ? and login_provider = ? and provider_key = ?"),
                _session.PrepareAsync("DELETE FROM logins_by_provider WHERE login_provider = ? AND provider_key = ?")
            }));
            _getLogins = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync("SELECT * FROM logins WHERE userId = ?"));
            _getLoginsByProvider = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync(
                "SELECT * FROM logins WHERE login_provider = ? AND provider_key = ?"));

            _getClaims = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync("SELECT * FROM claims WHERE userId = ?"));
            _addClaim = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync(
                "INSERT INTO claims (userid, type, value) VALUES (?, ?, ?)"));
            _removeClaim = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync(
                "DELETE FROM claims WHERE userId = ? AND type = ? AND value = ?"));
        }

        /// <summary>
        /// Insert a new user.
        /// </summary>
        public async Task CreateAsync(CassandraUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            // TODO:  Support uniqueness for usernames/emails at the C* level using LWT?

            PreparedStatement[] prepared = await _createUser;
            var batch = new BatchStatement();

            // INSERT INTO users ...
            batch.Add(prepared[0].Bind(user.Id, user.UserName, user.PasswordHash, user.SecurityStamp));

            // INSERT INTO users_by_username ...
            batch.Add(prepared[1].Bind(user.UserName, user.Id, user.PasswordHash, user.SecurityStamp));

            await _session.ExecuteAsync(batch).ConfigureAwait(false);
        }

        /// <summary>
        /// Update a user.
        /// </summary>
        public async Task UpdateAsync(CassandraUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            // TODO:  Currently we assume username will not change.  Support updating username?

            PreparedStatement[] prepared = await _updateUser;
            var batch = new BatchStatement();

            // UPDATE users ...
            batch.Add(prepared[0].Bind(user.PasswordHash, user.SecurityStamp, user.Id));

            // UPDATE users_by_username ...
            batch.Add(prepared[1].Bind(user.PasswordHash, user.SecurityStamp, user.UserName));

            await _session.ExecuteAsync(batch).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a user.
        /// </summary>
        public async Task DeleteAsync(CassandraUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            PreparedStatement[] prepared = await _deleteUser;
            var batch = new BatchStatement();

            // DELETE FROM users ...
            batch.Add(prepared[0].Bind(user.Id));

            // DELETE FROM users_by_username ...
            batch.Add(prepared[1].Bind(user.UserName));
            
            await _session.ExecuteAsync(batch).ConfigureAwait(false);
        }

        /// <summary>
        /// Finds a user by userId.
        /// </summary>
        public async Task<CassandraUser> FindByIdAsync(Guid userId)
        {
            PreparedStatement prepared = await _findById;
            BoundStatement bound = prepared.Bind(userId);

            RowSet rows = await _session.ExecuteAsync(bound).ConfigureAwait(false);
            return MapRowToCassandraUser(rows.SingleOrDefault());
        }

        /// <summary>
        /// Find a user by name (assumes usernames are unique).
        /// </summary>
        public async Task<CassandraUser> FindByNameAsync(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) throw new ArgumentException("userName cannot be null or empty", "userName");
            
            PreparedStatement prepared = await _findByName;
            BoundStatement bound = prepared.Bind(userName);

            RowSet rows = await _session.ExecuteAsync(bound).ConfigureAwait(false);
            return MapRowToCassandraUser(rows.SingleOrDefault());
        }

        private static CassandraUser MapRowToCassandraUser(Row row)
        {
            if (row == null) return null;

            return new CassandraUser
            {
                Id = row.GetValue<Guid>("userid"),
                UserName = row.GetValue<string>("username"),
                PasswordHash = row.GetValue<string>("password_hash"),
                SecurityStamp = row.GetValue<string>("security_stamp")
            };
        }

        /// <summary>
        /// Adds a user login with the specified provider and key
        /// </summary>
        public async Task AddLoginAsync(CassandraUser user, UserLoginInfo login)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (login == null) throw new ArgumentNullException("login");

            PreparedStatement[] prepared = await _addLogin;
            var batch = new BatchStatement();

            // INSERT INTO logins ...
            batch.Add(prepared[0].Bind(user.Id, login.LoginProvider, login.ProviderKey));

            // INSERT INTO logins_by_provider ...
            batch.Add(prepared[1].Bind(login.LoginProvider, login.ProviderKey, user.Id));

            await _session.ExecuteAsync(batch).ConfigureAwait(false);
        }

        /// <summary>
        /// Removes the user login with the specified combination if it exists
        /// </summary>
        public async Task RemoveLoginAsync(CassandraUser user, UserLoginInfo login)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (login == null) throw new ArgumentNullException("login");

            PreparedStatement[] prepared = await _removeLogin;
            var batch = new BatchStatement();

            // DELETE FROM logins ...
            batch.Add(prepared[0].Bind(user.Id, login.LoginProvider, login.ProviderKey));

            // DELETE FROM logins_by_provider ...
            batch.Add(prepared[1].Bind(login.LoginProvider, login.ProviderKey));
            
            await _session.ExecuteAsync(batch).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns the linked accounts for this user
        /// </summary>
        public async Task<IList<UserLoginInfo>> GetLoginsAsync(CassandraUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            PreparedStatement prepared = await _getLogins;
            BoundStatement bound = prepared.Bind(user.Id);

            RowSet rows = await _session.ExecuteAsync(bound).ConfigureAwait(false);
            return rows.Select(row => new UserLoginInfo(row.GetValue<string>("login_provider"), row.GetValue<string>("provider_key"))).ToList();
        }

        /// <summary>
        /// Returns the user associated with this login
        /// </summary>
        public async Task<CassandraUser> FindAsync(UserLoginInfo login)
        {
            if (login == null) throw new ArgumentNullException("login");

            PreparedStatement prepared = await _getLoginsByProvider;
            BoundStatement bound = prepared.Bind(login.LoginProvider, login.ProviderKey);

            RowSet loginRows = await _session.ExecuteAsync(bound).ConfigureAwait(false);
            Row loginResult = loginRows.FirstOrDefault();
            if (loginResult == null)
                return null;

            prepared = await _findById;
            bound = prepared.Bind(loginResult.GetValue<Guid>("userid"));

            RowSet rows = await _session.ExecuteAsync(bound).ConfigureAwait(false);
            return MapRowToCassandraUser(rows.SingleOrDefault());
        }

        /// <summary>
        /// Returns the claims for the user with the issuer set
        /// </summary>
        public async Task<IList<Claim>> GetClaimsAsync(CassandraUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            PreparedStatement prepared = await _getClaims;
            BoundStatement bound = prepared.Bind(user.Id);

            RowSet rows = await _session.ExecuteAsync(bound).ConfigureAwait(false);
            return rows.Select(row => new Claim(row.GetValue<string>("type"), row.GetValue<string>("value"))).ToList();
        }

        /// <summary>
        /// Add a new user claim
        /// </summary>
        public async Task AddClaimAsync(CassandraUser user, Claim claim)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (claim == null) throw new ArgumentNullException("claim");

            PreparedStatement prepared = await _addClaim;
            BoundStatement bound = prepared.Bind(user.Id, claim.Type, claim.Value);
            await _session.ExecuteAsync(bound).ConfigureAwait(false);
        }

        /// <summary>
        /// Remove a user claim
        /// </summary>
        public async Task RemoveClaimAsync(CassandraUser user, Claim claim)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (claim == null) throw new ArgumentNullException("claim");

            PreparedStatement prepared = await _removeClaim;
            BoundStatement bound = prepared.Bind(user.Id, claim.Type, claim.Value);

            await _session.ExecuteAsync(bound).ConfigureAwait(false);
        }

        /// <summary>
        /// Set the user password hash
        /// </summary>
        public Task SetPasswordHashAsync(CassandraUser user, string passwordHash)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (passwordHash == null) throw new ArgumentNullException("passwordHash");

            user.PasswordHash = passwordHash;
            return CompletedTask;
        }

        /// <summary>
        /// Get the user password hash
        /// </summary>
        public Task<string> GetPasswordHashAsync(CassandraUser user)
        {
            if (user == null) throw new ArgumentNullException("user");
            return Task.FromResult(user.PasswordHash);
        }

        /// <summary>
        /// Returns true if a user has a password set
        /// </summary>
        public Task<bool> HasPasswordAsync(CassandraUser user)
        {
            if (user == null) throw new ArgumentNullException("user");
            return string.IsNullOrEmpty(user.PasswordHash) ? FalseTask : TrueTask;
        }

        /// <summary>
        /// Set the security stamp for the user
        /// </summary>
        public Task SetSecurityStampAsync(CassandraUser user, string stamp)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (stamp == null) throw new ArgumentNullException("stamp");

            user.SecurityStamp = stamp;
            return CompletedTask;
        }

        /// <summary>
        /// Get the user security stamp
        /// </summary>
        public Task<string> GetSecurityStampAsync(CassandraUser user)
        {
            if (user == null) throw new ArgumentNullException("user");
            return Task.FromResult(user.SecurityStamp);
        }

        public Task SetTwoFactorEnabledAsync(TUser user, bool enabled)
        {
            if (user == null) throw new ArgumentNullException("user");

            user.IsTwoFactorEnabled = enabled;
            return Task.FromResult(0);
        }

        public Task<bool> GetTwoFactorEnabledAsync(TUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            return Task.FromResult(user.IsTwoFactorEnabled);
        }

        public Task<DateTimeOffset> GetLockoutEndDateAsync(TUser user)
        {
            if (user == null) throw new ArgumentNullException("user");
            if(user.LockoutEndDate == null) throw new InvalidOperationException("LockoutEndDate has no value.");

            return Task.FromResult(user.LockoutEndDate.Value);
        }

        public Task SetLockoutEndDateAsync(TUser user, DateTimeOffset lockoutEnd)
        {
            if (user == null) throw new ArgumentNullException("user");

            user.LockoutEndDate = lockoutEnd;
            return Task.FromResult(0);
        }

        public Task<int> IncrementAccessFailedCountAsync(TUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            user.AccessFailedCount++;
            return Task.FromResult(0);
        }

        public Task ResetAccessFailedCountAsync(TUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            user.AccessFailedCount = 0;
            return Task.FromResult(0);
        }

        public Task<int> GetAccessFailedCountAsync(TUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            return Task.FromResult(user.AccessFailedCount);
        }

        public Task<bool> GetLockoutEnabledAsync(TUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            return Task.FromResult(user.IsLockoutEnabled);
        }

        public Task SetLockoutEnabledAsync(TUser user, bool enabled)
        {
            if (user == null) throw new ArgumentNullException("user");

            user.IsLockoutEnabled = enabled;
            return Task.FromResult(0);
        }

        public Task SetEmailAsync(TUser user, string email)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (email == null) throw new ArgumentNullException("email");

            user.Email = email;
            return Task.FromResult(0);
        }

        public Task<string> GetEmailAsync(TUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            return Task.FromResult(user.Email);
        }

        public Task<bool> GetEmailConfirmedAsync(TUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            return Task.FromResult(user.EmailConfirmedOn != null);
        }

        public Task SetEmailConfirmedAsync(TUser user, bool confirmed)
        {
            if (user == null) throw new ArgumentNullException("user");

            if (confirmed)
            {
                user.EmailConfirmedOn = DateTime.Now;
            }
            else
            {
                user.EmailConfirmedOn = null;
            }
            return Task.FromResult(0);
        }

        public Task SetPhoneNumberAsync(TUser user, string phoneNumber)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (phoneNumber == null) throw new ArgumentNullException("phoneNumber");

            user.PhoneNumber = phoneNumber;
            return Task.FromResult(0);
        }

        public Task<string> GetPhoneNumberAsync(TUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            return Task.FromResult(user.PhoneNumber);
        }

        public Task<bool> GetPhoneNumberConfirmedAsync(TUser user)
        {
            if (user == null) throw new ArgumentNullException("user");

            return Task.FromResult(user.PhoneNumberConfirmedOn != null);
        }

        public Task SetPhoneNumberConfirmedAsync(TUser user, bool confirmed)
        {
            if (user == null) throw new ArgumentNullException("user");

            if (confirmed)
            {
                user.PhoneNumberConfirmedOn = DateTime.Now;
            }
            else
            {
                user.PhoneNumberConfirmedOn = null;
            }
            return Task.FromResult(0);
        }


        protected void Dispose(bool disposing)
        {
            _session.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
