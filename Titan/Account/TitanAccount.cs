using System;
using System.Collections.Generic;
using SteamKit2;
using SteamKit2.GC;
using SteamKit2.GC.CSGO.Internal;
using Titan.Json;
using Titan.MatchID.Live;
using Titan.Meta;

namespace Titan.Account
{
    
    // ReSharper disable InconsistentNaming
    public abstract class TitanAccount
    {

        public const uint TF2_APPID = 440;
        public const uint CSGO_APPID = 730;
        
        public const uint PENALTY_NONE = 0;
        public const uint PENALTY_TEAM_KILL_AT_ROUND_START = 3; // Speculation: may also be the 30min cooldown
        public const uint PENALTY_MATCH_ABANDON = 5; // Speculation: may also be the 2hr cooldown
        public const uint PENALTY_OVERWATCH_CONVICTED_MAJORLY_DISRUPTIVE = 10;
        public const uint PENALTY_OVERWATCH_CONVICTED_MINORLY_DISRUPTIVE = 11;
        //public const uint PENALTY_PERMANENTLY_UNTRUSTED_ANGLES = 13, // Only speculation at this point as I can't test it
        public const uint PENALTY_PERMANENTLY_UNTRUSTED_VAC = 14;

        ////////////////////////////////////////////////////
        // INIT
        ////////////////////////////////////////////////////

        public JsonAccounts.JsonAccount JsonAccount;

        internal TitanAccount(JsonAccounts.JsonAccount json)
        {
            JsonAccount = json;
        }

        ~TitanAccount()
        {
            if (IsRunning)
            {
                Stop();
            }
        }

        ////////////////////////////////////////////////////
        // TIME
        ////////////////////////////////////////////////////

        public long StartEpoch { get; set; }
        
        public bool IsRunning { get; set; }

        ////////////////////////////////////////////////////
        // GENERAL
        ////////////////////////////////////////////////////

        public abstract Result Start();
        public abstract void Stop();

        public abstract void OnConnected(SteamClient.ConnectedCallback callback);
        public abstract void OnDisconnected(SteamClient.DisconnectedCallback callback);

        public abstract void OnLoggedOn(SteamUser.LoggedOnCallback callback);
        public abstract void OnLoggedOff(SteamUser.LoggedOffCallback callback);

        ////////////////////////////////////////////////////
        // GAME COORDINATOR
        ////////////////////////////////////////////////////

        public void OnGCMessage(SteamGameCoordinator.MessageCallback callback)
        {
            var map = new Dictionary<uint, Action<IPacketGCMsg>>
            {
                { (uint) EGCBaseClientMsg.k_EMsgGCClientWelcome, OnClientWelcome },
                { (uint) ECsgoGCMsg.k_EMsgGCCStrike15_v2_ClientReportResponse, OnReportResponse },
                { (uint) ECsgoGCMsg.k_EMsgGCCStrike15_v2_MatchmakingGC2ClientHello, OnMatchmakingHelloResponse },
                { (uint) ECsgoGCMsg.k_EMsgGCCStrike15_v2_ClientCommendPlayerQueryResponse, OnCommendResponse },
                { (uint) ECsgoGCMsg.k_EMsgGCCStrike15_v2_MatchList, OnLiveGameRequestResponse }
            };

            if (map.TryGetValue(callback.EMsg, out var func))
            {
                func(callback.Message);
            }
        }

        public abstract void OnClientWelcome(IPacketGCMsg msg);
        public abstract void OnMatchmakingHelloResponse(IPacketGCMsg msg);
        public abstract void OnReportResponse(IPacketGCMsg msg);
        public abstract void OnCommendResponse(IPacketGCMsg msg);
        public abstract void OnLiveGameRequestResponse(IPacketGCMsg msg);

        ////////////////////////////////////////////////////
        // BOTTING SESSION INFORMATIONS
        ////////////////////////////////////////////////////

        public ReportInfo _reportInfo; // Setting this to private will cause it not to be visible for inheritated classes

        public void FeedReportInfo(ReportInfo info)
        {
            _reportInfo = info;
        }
        
        public CommendInfo _commendInfo; // Setting this to private will cause it not to be visible for inheritated classes

        public void FeedCommendInfo(CommendInfo info)
        {
            _commendInfo = info;
        }

        public LiveGameInfo _liveGameInfo; // Setting this to private will cause it not to be visible for inheritated classes
        public MatchInfo MatchInfo;

        public void FeedLiveGameInfo(LiveGameInfo info)
        {
            _liveGameInfo = info;
        }

        public uint GetAppID()
        {
            if (_reportInfo != null)
            {
                return _reportInfo.AppID;
            } 
            
            if (_commendInfo != null)
            {
                return _commendInfo.AppID;
            } 
            
            if (_liveGameInfo != null)
            {
                return _liveGameInfo.AppID;
            }

            return CSGO_APPID; // Default to CS:GO App ID
        }

        ////////////////////////////////////////////////////
        // PAYLOADS
        ////////////////////////////////////////////////////

        public ClientGCMsgProtobuf<CMsgGCCStrike15_v2_ClientReportPlayer> GetReportPayload()
        {
            var payload = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_ClientReportPlayer>(
                (uint) ECsgoGCMsg.k_EMsgGCCStrike15_v2_ClientReportPlayer
            )
            {
                Body =
                {
                    account_id = _reportInfo.SteamID.AccountID,
                    match_id = _reportInfo.MatchID,
                    rpt_aimbot = (uint) (_reportInfo.AimHacking ? 1 : 0),
                    rpt_wallhack = (uint) (_reportInfo.WallHacking ? 1 : 0),
                    rpt_speedhack = (uint) (_reportInfo.OtherHacking ? 1 : 0),
                    rpt_teamharm = (uint) (_reportInfo.Griefing ? 1 : 0),
                    rpt_textabuse = (uint) (_reportInfo.AbusiveText ? 1 : 0),
                    rpt_voiceabuse = (uint) (_reportInfo.AbusiveVoice ? 1 : 0)
                }
            };

            return payload;
        }

        public ClientGCMsgProtobuf<CMsgGCCStrike15_v2_ClientCommendPlayer> GetCommendPayload()
        {
            var payload = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_ClientCommendPlayer>(
                (uint) ECsgoGCMsg.k_EMsgGCCStrike15_v2_ClientCommendPlayer
            )
            {
                Body =
                {
                    account_id = _commendInfo.SteamID.AccountID,
                    match_id = 0,
                    commendation = new PlayerCommendationInfo
                    {
                        cmd_friendly = (uint) (_commendInfo.Friendly ? 1 : 0),
                        cmd_teaching = (uint) (_commendInfo.Teacher ? 1 : 0),
                        cmd_leader = (uint) (_commendInfo.Leader ? 1 : 0)
                    },
                    tokens = 0
                }
            };

            return payload;
        }

        public ClientGCMsgProtobuf<CMsgGCCStrike15_v2_MatchListRequestLiveGameForUser> GetLiveGamePayload()
        {
            var payload = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_MatchListRequestLiveGameForUser>(
                (uint) ECsgoGCMsg.k_EMsgGCCStrike15_v2_MatchListRequestLiveGameForUser
            ) 
            {
                Body =
                {
                    accountid = _liveGameInfo.SteamID.AccountID
                }
            };

            return payload;
        }

        public ClientGCMsgProtobuf<CMsgGCCStrike15_v2_MatchmakingClient2GCHello> GetMatchmakingHelloPayload()
        {
            // This payload doesn't have any explicit body values
            return new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_MatchmakingClient2GCHello>(
                (uint) ECsgoGCMsg.k_EMsgGCCStrike15_v2_MatchmakingClient2GCHello
            );
        }

    }
}
