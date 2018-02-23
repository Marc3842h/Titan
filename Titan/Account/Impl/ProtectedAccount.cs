﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using Eto.Forms;
using Serilog.Core;
using SteamAuth;
using SteamKit2;
using SteamKit2.GC;
using SteamKit2.GC.CSGO.Internal;
using SteamKit2.Internal;
using Titan.Json;
using Titan.Logging;
using Titan.MatchID.Live;
using Titan.Sentry;
using Titan.UI;
using Titan.UI._2FA;
using Titan.Util;

namespace Titan.Account.Impl
{
    public class ProtectedAccount : TitanAccount
    {

        private Logger _log;

        private int _reconnects;

        private SteamConfiguration _steamConfig;
        private Sentry.Sentry _sentry;

        private SharedSecret _sharedSecretGenerator;
        private string _authCode;
        private string _2FactorCode;
        private LoginKey _loginKey;

        private SteamClient _steamClient;
        private SteamUser _steamUser;
        private SteamFriends _steamFriends;
        private SteamGameCoordinator _gameCoordinator;
        private CallbackManager _callbacks;
        private TitanHandler _titanHandle;

        public Result Result { get; private set; } = Result.Unknown;

        public ProtectedAccount(JsonAccounts.JsonAccount json) : base(json)
        {
            _log = LogCreator.Create("GC - " + json.Username + (!Titan.Instance.Options.Secure ? " (Protected)" : ""));

            _steamConfig = SteamConfiguration.Create(builder =>
            {
                builder.WithConnectionTimeout(TimeSpan.FromMinutes(1));
                //builder.WithWebAPIKey(Titan.Instance.WebHandle.GetKey()); Is null at time of this creation - needs fix
            });
            
            _sentry = new Sentry.Sentry(this);
            _loginKey = new LoginKey(this);
            
            _steamClient = new SteamClient(_steamConfig);
            _callbacks = new CallbackManager(_steamClient);
            _steamUser = _steamClient.GetHandler<SteamUser>();
            _steamFriends = _steamClient.GetHandler<SteamFriends>();
            _gameCoordinator = _steamClient.GetHandler<SteamGameCoordinator>();

            // This clause excludes SteamKit debug mode as that mode is handeled seperately.
            // Normal debug mode doesn't equal SteamKit debug mode.
            if (Titan.Instance.Options.Debug)
            {
                _titanHandle = new TitanHandler();
                _steamClient.AddHandler(_titanHandle);
                
                // Initialize debug network sniffer when debug mode is enabled
                var dir = new DirectoryInfo(Path.Combine(Titan.Instance.DebugDirectory.ToString(), json.Username));
                if (!dir.Exists)
                {
                    dir.Create();
                }
                
                _steamClient.DebugNetworkListener = new NetHookNetworkListener(
                    dir.ToString()
                );
            }

            if (!string.IsNullOrWhiteSpace(JsonAccount.SharedSecret))
            {
                _sharedSecretGenerator = new SharedSecret(this);
            }

            _log.Debug("Successfully initialized account object for " + json.Username + ".");
        }

        public override Result Start()
        {
            Thread.CurrentThread.Name = JsonAccount.Username + " - " + (_reportInfo != null ? "Report" : "Commend");

            _callbacks.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            _callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            _callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            _callbacks.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            _callbacks.Subscribe<SteamGameCoordinator.MessageCallback>(OnGCMessage);
            _callbacks.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);
            _callbacks.Subscribe<SteamUser.LoginKeyCallback>(OnNewLoginKey);

            IsRunning = true;
            _steamClient.Connect();

            while (IsRunning)
            {
                _callbacks.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(500));
            }

            return Result;
        }

        public override void Stop()
        {
            _reportInfo = null;
            _commendInfo = null;
            _liveGameInfo = null;
            
            if (_steamFriends.GetPersonaState() != EPersonaState.Offline)
            {
                _steamFriends.SetPersonaState(EPersonaState.Offline);
            }

            if (_steamUser.SteamID != null)
            {
                _steamUser.LogOff();
            }

            if (_steamClient.IsConnected)
            {
                _steamClient.Disconnect();
            }

            IsRunning = false;
            
            Titan.Instance.ThreadManager.FinishBotting(this);
        }

        ////////////////////////////////////////////////////
        // CALLBACKS
        ////////////////////////////////////////////////////

        public override void OnConnected(SteamClient.ConnectedCallback callback)
        {
            _log.Debug("Successfully connected to Steam. Logging in...");
            
            byte[] hash = null;
            if (_sentry.Exists())
            {
                _log.Debug("Found previous Sentry file. Hashing it and sending it to Steam...");

                hash = _sentry.Hash();
            }
            else
            {
                _log.Debug("No Sentry file found. Titan will ask for a confirmation code...");
            }

            var loginID = RandomUtil.RandomUInt32();

            string loginKey = null;
            if (_loginKey.Exists())
            {
                loginKey = _loginKey.GetLastKey();
            }

            if (!Titan.Instance.Options.Secure)
            {
                _log.Debug("Logging in with Auth Code: {a} / 2FA Code {b} / Hash: {c} / LoginID: {d} / LoginKey: {e}",
                    _authCode, _2FactorCode, hash != null ? Convert.ToBase64String(hash) : null, loginID, loginKey);
            }
            
            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = JsonAccount.Username,
                Password = loginKey == null ? JsonAccount.Password : null,
                AuthCode = _authCode,
                TwoFactorCode = _2FactorCode,
                SentryFileHash = hash,
                LoginID = loginID,
                ShouldRememberPassword = true,
                LoginKey = loginKey
            });
        }

        public override void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            _reconnects++;

            if (_reconnects <= 5 && !callback.UserInitiated && 
               (Result != Result.Success && Result != Result.AlreadyLoggedInSomewhereElse || IsRunning))
            {
                _log.Debug("Disconnected from Steam. Retrying in 5 seconds... ({Count}/5)", _reconnects);

                Thread.Sleep(TimeSpan.FromSeconds(5));

                _steamClient.Connect();
            }
            else
            {
                _log.Debug("Successfully disconnected from Steam.");
                IsRunning = false;
            }
        }

        public override void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            switch (callback.Result)
            {
                case EResult.OK:
                    _log.Debug("Successfully logged in. Registering that we're playing CS:GO...");

                    _steamFriends.SetPersonaState(EPersonaState.Online);

                    var playGames = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
                    playGames.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
                    {
                        game_id = GetAppID()
                    });
                    _steamClient.Send(playGames);

                    Thread.Sleep(5000);

                    _log.Debug("Successfully registered playing CS:GO. Sending client hello to CS:GO services.");

                    var clientHello = new ClientGCMsgProtobuf<CMsgClientHello>(
                        (uint) EGCBaseClientMsg.k_EMsgGCClientHello
                    );
                    _gameCoordinator.Send(clientHello, GetAppID());
                    break;
                case EResult.AccountLoginDeniedNeedTwoFactor:
                    if (!string.IsNullOrWhiteSpace(JsonAccount.SharedSecret))
                    {
                        _log.Debug("A shared secret has been provided: automatically generating it...");
                        
                        _2FactorCode = _sharedSecretGenerator.GenerateCode();
                    }
                    else
                    {
                        _log.Information("Opening UI form to get the 2FA Steam Guard App Code...");

                        Application.Instance.Invoke(() => Titan.Instance.UIManager.ShowForm(
                            UIType.TwoFactorAuthentification, new _2FAForm(this)
                        ));

                        while (string.IsNullOrEmpty(_2FactorCode))
                        {
                            /* Wait until we receive the Steam Guard code from the UI */
                        }
                    }
 
                    if (!Titan.Instance.Options.Secure)
                    {
                        _log.Information("Received 2FA Code: {Code}", _2FactorCode);
                    }
                    break;
                case EResult.AccountLogonDenied:
                    _log.Information("Opening UI form to get the Auth Token from EMail...");

                    Application.Instance.Invoke(() => Titan.Instance.UIManager.ShowForm(
                        UIType.TwoFactorAuthentification, new _2FAForm(this, callback.EmailDomain)
                    ));

                    while (string.IsNullOrEmpty(_authCode))
                    {
                        /* Wait until we receive the Auth Token from the UI */
                    }

                    if (!Titan.Instance.Options.Secure)
                    {
                        _log.Information("Received Auth Token: {Code}", _authCode);
                    }
                    break;
                case EResult.ServiceUnavailable:
                    _log.Error("Steam is currently offline. Please try again later.");

                    Stop();
                    break;
                case EResult.RateLimitExceeded:
                    _log.Error("Steam Rate Limit has been reached. Please try it again in a few minutes...");

                    Result = Result.RateLimit;
                    Stop();
                    break;
                case EResult.TwoFactorCodeMismatch:
                case EResult.TwoFactorActivationCodeMismatch:
                case EResult.Invalid:
                    _log.Error("A invalid SteamGuard authentificator code has been provided. Aborting!");
                    
                    Result = Result.Code2FAWrong;
                    Stop();
                    break;
                default:
                    _log.Error("Unable to logon to account: {Result}: {ExtendedResult}", callback.Result, callback.ExtendedResult);

                    Stop();
                    break;
            }
        }

        public override void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            if (callback.Result == EResult.LoggedInElsewhere || callback.Result == EResult.AlreadyLoggedInElsewhere)
            {
                Result = Result.AlreadyLoggedInSomewhereElse;
            }

            if (Result == Result.AlreadyLoggedInSomewhereElse)
            {
                _log.Warning("Account is already logged on somewhere else. Skipping...");
            }
            else
            {
                _log.Debug("Successfully logged off from Steam: {Result}", callback.Result);
            }
        }

        public void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback)
        {
            _log.Debug("Updating Steam sentry file...");

            if (_sentry.Save(callback.Offset, callback.Data, callback.BytesToWrite, out var hash, out var size))
            {
                _steamUser.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
                {
                    JobID = callback.JobID,
                    FileName = callback.FileName,
                    
                    BytesWritten = callback.BytesToWrite,
                    FileSize = size,
                    Offset = callback.Offset,
                    
                    Result = EResult.OK,
                    LastError = 0,
                    
                    OneTimePassword = callback.OneTimePassword,
                    
                    SentryFileHash = hash
                });
                
                _log.Information("Successfully updated Steam sentry file.");
            }
            else
            {
                _log.Error("Could not save sentry file. Titan will ask again for Steam Guard code on next attempt.");
            }
        }

        public void OnNewLoginKey(SteamUser.LoginKeyCallback callback)
        {
            if (!Titan.Instance.Options.Secure)
            {
                _log.Debug("Received new login key: {key}", callback.LoginKey);
            }

            _loginKey.Save(callback.LoginKey);
            _steamUser.AcceptNewLoginKey(callback);
        }

        public override void OnClientWelcome(IPacketGCMsg msg)
        {
            var welcome = new ClientGCMsgProtobuf<CMsgClientWelcome>(msg);
            
            _log.Debug("Received welcome from CS:GO GC version {v} (Connected to {loc}). " +
                       "Sending hello the CS:GO's matchmaking service.",
                       welcome.Body.version, welcome.Body.location.country);
            
            _gameCoordinator.Send(GetMatchmakingHelloPayload(), GetAppID());
        }

        public override void OnMatchmakingHelloResponse(IPacketGCMsg msg)
        {
            var response = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_MatchmakingGC2ClientHello>(msg);

            if (response.Body.penalty_reasonSpecified)
            {
                switch (response.Body.penalty_reason)
                {
                    case PENALTY_OVERWATCH_CONVICTED_MAJORLY_DISRUPTIVE:
                    {
                        _log.Error("This account has been convicted by Overwatch as majorly disruptive and has been " +
                                   "permanently banned. Botting with banned accounts is not possible and will not " +
                                   "give succesful results. Aborting!");
                        Result = Result.AccountBanned;

                        Stop();
                        return;
                    }
                    case PENALTY_OVERWATCH_CONVICTED_MINORLY_DISRUPTIVE:
                    {
                        var penalty = TimeSpan.FromSeconds(response.Body.penalty_seconds);

                        _log.Error("This account has been convicted by Overwatch as majorly minorly and has been " +
                                   "banned for {days} more days. Botting with banned accounts is not possible and " +
                                   "will not give succesful results. Aborting!", penalty.Days);
                        Result = Result.AccountBanned;

                        Stop();
                        return;
                    }
                    case PENALTY_PERMANENTLY_UNTRUSTED_VAC:
                    {
                        _log.Error("This account is permanently untrusted. Botting with banned accounts is not " +
                                   "possible and will not give succesful results. Aborting!");
                        Result = Result.AccountBanned;

                        Stop();
                        return;
                    }
                    default:
                    {
                        if (response.Body.penalty_secondsSpecified)
                        {
                            var penalty = TimeSpan.FromSeconds(response.Body.penalty_seconds);

                            // 604800 seconds = 7 days
                            // If the penalty seconds are over 7 days, the account is permanently banned.
                            // If not, the account has received a temporary Matchmaking cooldown
                            if (penalty.Seconds <= 604800)
                            {
                                string timeString;
                                if (penalty.Minutes >= 60)
                                {
                                    timeString = penalty.Hours + " Hours";
                                }
                                else
                                {
                                    timeString = penalty.Minutes + " Minutes";
                                }

                                _log.Error("This account has received a Matchmaking cooldown. Botting with banned " +
                                           "accounts is not possible and will not give successful results. Aborting!");
                                _log.Error("The matchmaking cooldown of this account will end in {end}.", timeString);
                                Result = Result.AccountBanned;

                                Stop();
                                return;
                            }
                        }

                        _log.Error("This account has been permanently banned from CS:GO. Botting with banned " +
                                   "accounts is not possible and will not give successful results. Aborting!");
                        Result = Result.AccountBanned;

                        Stop();
                        return;
                    }
                }
            }

            // When the CS:GO GC sends a vac_banned (type 2) but not a penalty_reason (type 0)
            // the account has received a yellow "This account has been banned by Overwatch"
            // banner in-game and has no longer the ability to report or commend.
            if (response.Body.vac_bannedSpecified && !response.Body.penalty_reasonSpecified && 
                response.Body.vac_banned == 2 && !response.Body.penalty_secondsSpecified)
            {
                _log.Error("This account has been banned by Valve Anti Cheat. Botting with banned " +
                           "accounts is not possible and will not give successfull results. Aborting!");
                Result = Result.AccountBanned;
                
                Stop();
                return;
            }
            
            var type = _liveGameInfo != null ? "Live Game Request" : (_reportInfo != null ? "Report" : "Commend");
            _log.Debug("Received hello from CS:GO matchmaking services. Authentificated as {id}. Sending {type}.",
                       response.Body.account_id, type);
            
            if (_liveGameInfo != null)
            {
                _gameCoordinator.Send(GetLiveGamePayload(), GetAppID());
            }
            else if (_reportInfo != null)
            {
                _gameCoordinator.Send(GetReportPayload(), GetAppID());
            }
            else
            {
                _gameCoordinator.Send(GetCommendPayload(), GetAppID());
            }
        }

        public override void OnReportResponse(IPacketGCMsg msg)
        {
            var response = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_ClientReportResponse>(msg);

            if (_reportInfo != null)
            {
                _log.Information("Successfully reported. Confirmation ID: {ID}", response.Body.confirmation_id);
            }
            else
            {
                _log.Information("Successfully commended {Target} with {Pretty}.",
                    _commendInfo.SteamID.ConvertToUInt64(), _commendInfo.ToPrettyString());
            }

            Result = Result.Success;

            Stop();
        }

        public override void OnCommendResponse(IPacketGCMsg msg)
        {
            _log.Information("Successfully commended target {Target} with {Pretty}.", 
                _commendInfo.SteamID.ConvertToUInt64(), _commendInfo.ToPrettyString());

            Result = Result.Success;

            Stop();
        }

        public override void OnLiveGameRequestResponse(IPacketGCMsg msg)
        {
            var response = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_MatchList>(msg);

            if (response.Body.matches.Count >= 1)
            {
                var matchInfos = response.Body.matches.Select(match => new MatchInfo
                    {
                        MatchID = match.matchid,
                        MatchTime = match.matchtime,
                        WatchableMatchInfo = match.watchablematchinfo,
                        RoundsStats = match.roundstatsall
                    }
                ).ToList();

                MatchInfo = matchInfos[0]; // TODO: Maybe change this into a better than meme than just using the 0 index

                _log.Information("Received live game Match ID: {MatchID}", MatchInfo.MatchID);

                Result = Result.Success;
            }
            else
            {
                MatchInfo = new MatchInfo
                {
                    MatchID = 8,
                    MatchTime = 0,
                    WatchableMatchInfo = null,
                    RoundsStats = null
                };
                
                Result = Result.NoMatches;
            }
            
            Stop();
        }

        public void FeedWithAuthToken(string authToken)
        {
            _authCode = authToken;
        }

        public void FeedWith2FACode(string twofactorCode)
        {
            _2FactorCode = twofactorCode;
        }

    }
}
