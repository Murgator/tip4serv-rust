﻿/***********************************************************************************************************************/
/*** DO NOT edit this file! Edit the files under `oxide/config` and/or `oxide/lang`, created once plugin has loaded. ***/
/***********************************************************************************************************************/

using System.Collections.Generic;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core;
using Oxide.Core.Libraries;
using System;
using System.Text;
using System.Security.Cryptography;

namespace Oxide.Plugins
{
    [Info("Tip4serv", "Murgator & Duster", "1.3.2")]
    [Description("Allows Admin to monetize their Rust server from their Tip4serv store")]
    public class Tip4serv : CovalencePlugin
    {
        private class PluginConfig
        {
            public int request_interval_in_minutes;
            public string configkey;
            public string order_received_text;
        }
        [Serializable]
        public class ResponseData
        {
            public string date;
            public string action;
            public Dictionary<int, int> cmds;
            public int status;
            public string rust_username;
        }
        [Serializable]
        public class Payments
        {
            public string player;
            public string action;
            public string id;
            public string steamid;
            public PaymentCmd[] cmds;
        }
        [Serializable]
        public class PaymentCmd
        {
            public string str;
            public int id;
            public int state;
        }
        private String key_msg = "Please set the config key to a valid key in your config/Tip4Serv.json file. Make sure you have copied the entire key on Tip4Serv.com (Ctrl+A then CTRL+C)";
        private bool Stopped = false;
        private PluginConfig config;
        protected override void LoadDefaultConfig()
        {
            LogWarning("Creating a new configuration file");
            Config.WriteObject(GetDefaultConfig(), true);
        }
        private PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                request_interval_in_minutes = 1,
                configkey = "YOUR_CONFIG_KEY",
                order_received_text = "[#cyan][Tip4serv][/#] You have received your order. Thank you !"
            };
        }
        private void Loaded()
        {
            #if !RUST
               LogError("This plugin only works for the Rust Game");
               Stopped = true;
            #else
               Tip4Print("Tip4serv plugin has started");
               config = Config.ReadObject<PluginConfig>();
            #endif
        }
        private void Unload()
        {
            key_msg = null;
        }
        void OnServerInitialized()
        {
            if (!Stopped)
            {
                //check Tip4serv connection on script start
                string[] key_part = config.configkey.Split('.');
                if (key_part.Length != 3)
                {
                    Tip4Print(key_msg);
                    return;
                }
                check_pending_commands(key_part, GetUnixTime(), "no");
                timer.Repeat((float)config.request_interval_in_minutes * 60f, 0, () => {
                    PaymentChecker();
                });
            }
        }
        private void PaymentChecker()
        {
            string[] key_part = config.configkey.Split('.');
            if (key_part.Length != 3)
            {
                Tip4Print(key_msg);
                return;
            }
            check_pending_commands(key_part, GetUnixTime(), "yes");
        }
        private string GetUnixTime()
        {
            long unixTime = ((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds();
            return unixTime.ToString();
        }
        private void Tip4Print(string content)
        {
            LogWarning(content);
        }
        private void check_pending_commands(string[] key_parts, string timestamp, string get_cmd)
        {
            //HMAC calculation
            string HMAC = calculateHMAC(key_parts, timestamp);
            //get last infos from the json file
            Dictionary<string, ResponseData> response = LoadFile("tip4serv_response");
            string json_encoded = "";
            if (response.Count > 0)
            {
                json_encoded = System.Uri.EscapeDataString(Utility.ConvertToJson(response));
            }
            //request tip4serv
            string statusUrl = "https://api.tip4serv.com/payments_api_v2.php?id=" + key_parts[0] + "&time=" + timestamp + "&json=" + json_encoded + "&get_cmd=" + get_cmd;
            Dictionary<string, string> Headers = new Dictionary<string, string> { { "Authorization", HMAC } };
            webrequest.Enqueue(statusUrl, null, (code, HTTPresponse) => {
                if (code != 200 || HTTPresponse == null)
                {
                    if (get_cmd == "no")
                    {
                        Tip4Print("Tip4serv API is temporarily unavailable, maybe you are making too many requests. Please try again later");
                    }
                    return;
                }
                //tip4serv connect
                if (get_cmd == "no")
                {
                    Tip4Print(HTTPresponse);
                    return;
                }
                response.Clear();
                //check for errors
                if (HTTPresponse.Contains("No pending payments found"))
                {
                    Interface.Oxide.DataFileSystem.WriteObject("tip4serv_response", response);
                    return;
                }
                else if (HTTPresponse.StartsWith("\"[Tip4serv "))
                {
                    Tip4Print(HTTPresponse);
                    return;
                }
                //clear old json infos
                Interface.Oxide.DataFileSystem.WriteObject("tip4serv_response", response);
                var json_decoded = Utility.ConvertFromJson<List<Payments>>(HTTPresponse);
                //loop customers
                for (int i = 0; i < json_decoded.Count; i++)
                {
                    ResponseData new_obj = new ResponseData();
                    Dictionary<int, int> new_cmds = new Dictionary<int, int>();
                    string payment_id = json_decoded[i].id;
                    new_obj.date = DateTime.Now.ToString();
                    new_obj.action = json_decoded[i].action;
                    new_obj.rust_username = "";
                    //check if player is online
                    IPlayer player_infos = checkifPlayerIsLoaded(json_decoded[i].steamid);
                    if (player_infos != null)
                    {
                        new_obj.rust_username = player_infos.Name;
                        player_infos.Message(config.order_received_text);
                    }
                    if (!json_decoded[i].cmds.IsEmpty())
                    {
                        for (int j = 0; j < json_decoded[i].cmds.Length; j++)
                        {
                            //do not run this command if the player must be online
                            if (player_infos == null && (json_decoded[i].cmds[j].str.Contains("{") || (json_decoded[i].cmds[j].state == 1)))
                            {
                                new_obj.status = 14;
                            }
                            else
                            {
                                if (json_decoded[i].cmds[j].str.Contains("{rust_username}"))
                                {
                                    if (player_infos != null)
                                        json_decoded[i].cmds[j].str = json_decoded[i].cmds[j].str.Replace("{rust_username}", player_infos.Name);
                                }
                                string[] empty = { };
                                exe_command(json_decoded[i].cmds[j].str, empty);
                                new_cmds[json_decoded[i].cmds[j].id] = 3;
                            }
                        }
                        new_obj.cmds = new_cmds;
                        if (new_obj.status == 0)
                        {
                            new_obj.status = 3;
                        }
                        response[payment_id] = new_obj;
                    }
                }
                //save the new json file
                Interface.Oxide.DataFileSystem.WriteObject("tip4serv_response", response);
            }, this, RequestMethod.GET, Headers);
        }
        private Dictionary<string, ResponseData> LoadFile(string path)
        {
            Dictionary<string, ResponseData> response = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, ResponseData>>("tip4serv_response");
            return response;
        }
        private string calculateHMAC(string[] key_parts, string timestamp)
        {
            HMACSHA256 Encryptor = new HMACSHA256(Encoding.ASCII.GetBytes(key_parts[1]));
            key_parts[1] = "";
            string Total_Key = string.Join("", key_parts);
            Total_Key += timestamp;
            var signature = Encryptor.ComputeHash(Encoding.ASCII.GetBytes(Total_Key));
            var HMACstr = BitConverter.ToString(signature).Replace("-", "").ToLower();
            HMACstr = Convert.ToBase64String(Encoding.ASCII.GetBytes(HMACstr));
            return HMACstr;
        }
        private IPlayer checkifPlayerIsLoaded(string steam_id)
        {
            IPlayer SteamPlayer = covalence.Players.FindPlayerById(steam_id);
            if (SteamPlayer == null)
            {
                return null;
            }
            if (SteamPlayer.IsConnected)
            {
                return SteamPlayer;
            }
            else
            {
                return null;
            }
        }
        private void exe_command(string cmd, string[] CmdArgs)
        {
            try
            {
                server.Command(cmd, CmdArgs);
            }
            catch
            {
                return;
            }
        }
    }
}