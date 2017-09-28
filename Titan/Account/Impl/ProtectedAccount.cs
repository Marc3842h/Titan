﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
using Titan.UI;
using Titan.UI._2FA;
using Titan.Util;

namespace Titan.Account.Impl
{
    public class ProtectedAccount : TitanAccount
    {

        private Logger _log;

        private int _reconnects;

        private Sentry.Sentry _sentry;
        
        private SteamGuardAccount _sgAccount;
        private string _authCode;
        private string _2FactorCode;

        private SteamClient _steamClient;
        private SteamUser _steamUser;
        private SteamFriends _steamFriends;
        private SteamGameCoordinator _gameCoordinator;
        private CallbackManager _callbacks;

        public Result Result { get; private set; }

        public ProtectedAccount(JsonAccounts.JsonAccount json) : base(json)
        {
            _log = LogCreator.Create("GC - " + json.Username + " (Protected)");

            _sentry = new Sentry.Sentry(this);
            
            _steamClient = new SteamClient();
            _callbacks = new CallbackManager(_steamClient);
            _steamUser = _steamClient.GetHandler<SteamUser>();
            _steamFriends = _steamClient.GetHandler<SteamFriends>();
            _gameCoordinator = _steamClient.GetHandler<SteamGameCoordinator>();

            // Initialize debug network sniffer when debug mode is enabled
            if(Titan.Instance.Options.Debug)
            {
                var dir = new DirectoryInfo(Path.Combine(Titan.Instance.DebugDirectory.ToString(), json.Username));
                if(!dir.Exists)
                {
                    dir.Create();
                }
                
                _steamClient.DebugNetworkListener = new NetHookNetworkListener(
                    dir.ToString()
                );
            }

            if(json.SharedSecret != null)
            {
                _sgAccount = new SteamGuardAccount
                {
                    SharedSecret = json.SharedSecret
                };
            }

            _log.Debug("Successfully initialized account object for " + json.Username + ".");
        }

        ~ProtectedAccount()
        {
            if(IsRunning)
            {
                Stop();
            }
        }

        public override Result Start()
        {
            Thread.CurrentThread.Name = JsonAccount.Username + " - " + (_reportInfo != null ? "Report" :"Commend");

            _callbacks.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            _callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            _callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            _callbacks.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            _callbacks.Subscribe<SteamGameCoordinator.MessageCallback>(OnGCMessage);
            _callbacks.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);

            IsRunning = true;
            _steamClient.Connect();

            while(IsRunning)
            {
                _callbacks.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }

            return Result;
        }

        public override void Stop()
        {
            _reportInfo = null;
            _commendInfo = null;
            _liveGameInfo = null;
            _idleInfo = null;
            
            if(_steamFriends.GetPersonaState() == EPersonaState.Online)
            {
                _steamFriends.SetPersonaState(EPersonaState.Offline);
            }

            if(_steamUser.SteamID != null)
            {
                _steamUser.LogOff();
            }

            if(_steamClient.IsConnected)
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
            _log.Debug("Sentry has been activated for this account. Checking if a sentry file " +
                       "exists and hashing it...");

            var hash = _sentry.Hash();
                
            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = JsonAccount.Username,
                Password = JsonAccount.Password,
                AuthCode = _authCode,
                TwoFactorCode = _2FactorCode,
                SentryFileHash = hash.Length > 0 ? hash : null,
                LoginID = RandomUtil.RandomUInt32()
            });
        }

        public override void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            _reconnects++;

            if(_reconnects <= 5 && (Result != Result.Success ||
               Result != Result.AlreadyLoggedInSomewhereElse || IsRunning))
            {
                _log.Information("Disconnected from Steam. Retrying in 5 seconds... ({Count}/5)", _reconnects);

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
            switch(callback.Result)
            {
                case EResult.OK:
                    _log.Debug("Successfully logged in. Checking for any VAC or game bans...");

                    var banInfo = Titan.Instance.BanManager.GetBanInfoFor(_steamUser.SteamID.ConvertToUInt64());
                    if(banInfo != null && (banInfo.VacBanned || banInfo.GameBanCount > 0))
                    {
                        _log.Warning("The account has a ban on record. " +
                                     "If the VAC/Game ban ban is from CS:GO, a {Mode} is not possible. " +
                                     "Proceeding with caution.", _reportInfo != null ? "report" :"commend");
                        Result = Result.AccountBanned;
                    }

                    _log.Debug("Registering that we're playing CS:GO...");

                    _steamFriends.SetPersonaState(EPersonaState.Online);

                    var playGames = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
                    playGames.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
                    {
                        game_id = 730
                    });
                    _steamClient.Send(playGames);

                    Thread.Sleep(5000);

                    _log.Debug("Successfully registered playing CS:GO. Sending client hello to CS:GO services.");

                    var clientHello = new ClientGCMsgProtobuf<CMsgClientHello>((uint) EGCBaseClientMsg.k_EMsgGCClientHello);
                    _gameCoordinator.Send(clientHello, 730);
                    break;
                case EResult.AccountLoginDeniedNeedTwoFactor:
                    _log.Information("Opening UI form to get the 2FA Steam Guard App Code...");

                    if(_sgAccount != null)
                    {
                        _log.Debug("A shared secret has been provided: automaticly generating it...");
                        
                        _2FactorCode = _sgAccount.GenerateSteamGuardCode();
                    }
                    else
                    {

                        Application.Instance.Invoke(() => Titan.Instance.UIManager.ShowForm(
                            new TwoFactorAuthForm(Titan.Instance.UIManager, this, null))
                        );

                        while(string.IsNullOrEmpty(_2FactorCode))
                        {
                            /* Wait until the Form inputted the 2FA code from the Steam Guard App */
                        }
                        
                    }

                    _log.Information("Received 2FA Code: {Code}", _2FactorCode);
                    break;
                case EResult.AccountLogonDenied:
                    _log.Information("Opening UI form to get the Auth Token from EMail...");

                    Application.Instance.Invoke(() => Titan.Instance.UIManager.ShowForm(
                        new TwoFactorAuthForm(Titan.Instance.UIManager, this, callback.EmailDomain))
                    );

                    while(string.IsNullOrEmpty(_authCode))
                    {
                        /* Wait until the Form inputted the Auth code from the Email Steam sent */
                    }

                    _log.Information("Received Auth Token: {Code}", _authCode);
                    break;
                case EResult.ServiceUnavailable:
                    _log.Error("Steam is currently offline. Please try again later.");

                    Stop();

                    IsRunning = false;
                    break;
                case EResult.RateLimitExceeded:
                    _log.Debug("Steam Rate Limit has been reached. Please try it again in a few minutes...");

                    Stop();

                    IsRunning = false;
                    Result = Result.RateLimit;
                    break;
                default:
                    _log.Error("Unable to logon to account: {Result}: {ExtendedResult}", callback.Result, callback.ExtendedResult);

                    Stop();

                    IsRunning = false;
                    break;
            }
        }

        public override void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            if(callback.Result == EResult.LoggedInElsewhere || callback.Result == EResult.AlreadyLoggedInElsewhere)
                Result = Result.AlreadyLoggedInSomewhereElse;

            if(Result == Result.AlreadyLoggedInSomewhereElse)
                _log.Warning("Account is already logged on somewhere else. Skipping...");
            else
                _log.Debug("Successfully logged off from Steam: {Result}", callback.Result);
        }

        public void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback)
        {
            _log.Debug("Checking if a sentry file exists...");

            var hash = _sentry.Save(callback.Offset, callback.Data, callback.BytesToWrite, out int size);

            _log.Debug("Successfully opened / created sentry file. Hash: {Hash}", Encoding.UTF8.GetString(hash));

            _steamUser.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
            {
                JobID = callback.JobID,
                FileName = callback.FileName,
                BytesWritten = callback.BytesToWrite,
                FileSize = size,
                Offset = callback.Offset,
                Result = hash.Length > 0 ? EResult.OK : EResult.Fail,
                LastError = hash.Length > 0 ? 0 : (int) EResult.Fail,
                OneTimePassword = callback.OneTimePassword,
                SentryFileHash = hash.Length > 0 ? hash : null
            });
        }

        public override void OnGCMessage(SteamGameCoordinator.MessageCallback callback)
        {
            var map = new Dictionary<uint, Action<IPacketGCMsg>>
            {
                { (uint) EGCBaseClientMsg.k_EMsgGCClientWelcome, OnClientWelcome },
                { (uint) ECsgoGCMsg.k_EMsgGCCStrike15_v2_ClientReportResponse, OnReportResponse },
                { (uint) ECsgoGCMsg.k_EMsgGCCStrike15_v2_ClientCommendPlayerQueryResponse, OnCommendResponse },
                { (uint) ECsgoGCMsg.k_EMsgGCCStrike15_v2_MatchList, OnLiveGameRequestResponse }
            };

            if(map.TryGetValue(callback.EMsg, out Action<IPacketGCMsg> func))
            {
                func(callback.Message);
            }
        }

        public override void OnClientWelcome(IPacketGCMsg msg)
        {
            _log.Debug("Successfully received client hello from CS:GO services. Sending {Mode}...",
                _liveGameInfo != null ? "Live Game Request" : (_reportInfo != null ? "Report" : "Commend"));

            
            if(_liveGameInfo != null)
            {
                _gameCoordinator.Send(GetLiveGamePayload(), 730);
            }
            else if(_reportInfo != null)
            {
                _gameCoordinator.Send(GetReportPayload(), 730);
            }
            else
            {
                _gameCoordinator.Send(GetCommendPayload(), 730);
            }
        }

        public override void OnReportResponse(IPacketGCMsg msg)
        {
            var response = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_ClientReportResponse>(msg);

            if(_reportInfo != null)
            {
                _log.Information("Successfully reported. Confirmation ID: {ID}", response.Body.confirmation_id);
            }
            else
            {
                _log.Information("Successfully commended {Target} with a Leader, Friendly and a Teacher.",
                    _commendInfo.SteamID.ConvertToUInt64());
            }

            Result = Result.Success;

            Stop();
        }

        public override void OnCommendResponse(IPacketGCMsg msg)
        {
            _log.Information("Successfully commended target {Target}.", _commendInfo.SteamID.ConvertToUInt64());

            Result = Result.Success;

            Stop();
        }

        public override void OnLiveGameRequestResponse(IPacketGCMsg msg)
        {
            var response = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_MatchList>(msg);

            if(response.Body.matches.Count >= 1)
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
