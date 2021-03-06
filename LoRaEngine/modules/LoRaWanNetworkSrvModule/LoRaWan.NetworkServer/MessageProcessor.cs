﻿using Microsoft.Azure.Devices.Client;
using Newtonsoft.Json.Linq;
using PacketManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace LoRaWan.NetworkServer
{
    public class MessageProcessor : IDisposable
    {
       

        private DateTime startTimeProcessing;
     
        private static string GatewayID;


        public async Task processMessage(byte[] message)
        {
            startTimeProcessing = DateTime.UtcNow;

            //gate the edge device id for checking if the device is linked to a specific gateway
            if (string.IsNullOrEmpty(GatewayID))
            {
                GatewayID = Environment.GetEnvironmentVariable("IOTEDGE_DEVICEID");
            }

            LoRaMessage loraMessage = new LoRaMessage(message);

            byte[] udpMsgForPktForwarder = new Byte[0];

            if (!loraMessage.isLoRaMessage)
            {
                udpMsgForPktForwarder = ProcessNonLoraMessage(loraMessage);
            }
            else
            {
                //join message
                if (loraMessage.loRaMessageType == LoRaMessageType.JoinRequest)
                {
                    udpMsgForPktForwarder = await ProcessJoinRequest(loraMessage);

                }

                //normal message
                else if( loraMessage.loRaMessageType ==LoRaMessageType.UnconfirmedDataUp || loraMessage.loRaMessageType == LoRaMessageType.ConfirmedDataUp)
                {
                    udpMsgForPktForwarder = await ProcessLoraMessage(loraMessage);

                }

            }
          

            //send reply to pktforwarder
            await UdpServer.UdpSendMessage(udpMsgForPktForwarder);
        }

        private byte[] ProcessNonLoraMessage(LoRaMessage loraMessage)
        {
            byte[] udpMsgForPktForwarder = new byte[0];
            if (loraMessage.physicalPayload.identifier == PhysicalIdentifier.PULL_DATA)
            {
               

                PhysicalPayload pullAck = new PhysicalPayload(loraMessage.physicalPayload.token, PhysicalIdentifier.PULL_ACK, null);

                udpMsgForPktForwarder = pullAck.GetMessage();

            }

            return udpMsgForPktForwarder;
        }
        private async Task<byte[]> ProcessLoraMessage(LoRaMessage loraMessage)
        {
            byte[] udpMsgForPktForwarder = new byte[0];
            string devAddr = BitConverter.ToString(loraMessage.payloadMessage.devAddr).Replace("-", "");
            Message c2dMsg= null;



            Cache.TryGetValue(devAddr, out LoraDeviceInfo loraDeviceInfo);

           

            if (loraDeviceInfo == null)
            {
                Console.WriteLine($"Processing message from device: {devAddr} not in cache"); 

                loraDeviceInfo = await LoraDeviceInfoManager.GetLoraDeviceInfoAsync(devAddr);         

                Cache.AddToCache(devAddr, loraDeviceInfo);

            }
            else
            {
                Console.WriteLine($"Processing message from device: {devAddr} in cache");

            }

      

            if (loraDeviceInfo.IsOurDevice)
            {
                //either there is no gateway linked to the device or the gateway is the one that the code is running
                if (String.IsNullOrEmpty(loraDeviceInfo.GatewayID) || loraDeviceInfo.GatewayID.ToUpper() == GatewayID.ToUpper())
                {
                    if (loraMessage.CheckMic(loraDeviceInfo.NwkSKey))
                    {


                        if (loraDeviceInfo.HubSender == null)
                        {

                            loraDeviceInfo.HubSender = new IoTHubSender(loraDeviceInfo.DevEUI, loraDeviceInfo.PrimaryKey);

                        }


                        UInt16 fcntup = BitConverter.ToUInt16(((LoRaPayloadStandardData)loraMessage.payloadMessage).fcnt, 0);

                      
                        //check if the frame counter is valid: either is above the server one or is an ABP device resetting the counter (relaxed seqno checking)
                        if (fcntup > loraDeviceInfo.FCntUp  || (fcntup == 1 && String.IsNullOrEmpty(loraDeviceInfo.AppEUI)))
                        {
                            Console.WriteLine($"Valid frame counter, msg: {fcntup} server: {loraDeviceInfo.FCntUp}");


                            string decryptedMessage = null;
                            try
                            {
                                decryptedMessage = loraMessage.DecryptPayload(loraDeviceInfo.AppSKey);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Failed to decrypt message: {ex.Message}");
                            }



                            Rxpk rxPk = ((UplinkPktFwdMessage)loraMessage.loraMetadata.fullPayload).rxpk[0];


                            dynamic fullPayload = JObject.FromObject(rxPk);

                            string jsonDataPayload="";

                            //todo ronnie we may add these fields to rxPk?

                            if (String.IsNullOrEmpty(loraDeviceInfo.SensorDecoder))
                            {
                                jsonDataPayload = decryptedMessage;
                                fullPayload.data = jsonDataPayload;
                            }
                            else
                            {
                                jsonDataPayload = LoraDecoders.DecodeMessage(decryptedMessage, loraDeviceInfo.SensorDecoder);
                                fullPayload.data = JObject.Parse(jsonDataPayload);
                            }


                            fullPayload.EUI = loraDeviceInfo.DevEUI;
                            fullPayload.gatewayId = GatewayID;

                            //todo check what the other ts are if milliseconds or seconds
                            fullPayload.edgets = (long)((startTimeProcessing - new DateTime(1970, 1, 1)).TotalMilliseconds);


                            string iotHubMsg = fullPayload.ToString(Newtonsoft.Json.Formatting.None);



                            Console.WriteLine($"Sending message '{jsonDataPayload}' to hub...");

                            await loraDeviceInfo.HubSender.SendMessageAsync(iotHubMsg);

                            loraDeviceInfo.FCntUp = fcntup;


                           



                        }
                        else
                        {
                            Console.WriteLine($"Invalid frame counter, msg: {fcntup} server: {loraDeviceInfo.FCntUp}");
                        }

                        //start checking for new c2d message, we do it even if the fcnt is invalid so we support replying to the ConfirmedDataUp
                        //todo ronnie should we wait up to 900 msec?
                        c2dMsg = await loraDeviceInfo.HubSender.ReceiveAsync(TimeSpan.FromMilliseconds(20));

                        byte[] bytesC2dMsg = null;
                        byte[] fport = null;
                        byte[] fctl = new byte[1] { 32 };

                        //check if we got a c2d message to be added in the ack message and preprare the message
                        if (c2dMsg != null)
                        {
                            //check if there is another message
                            var secondC2dMsg = await loraDeviceInfo.HubSender.ReceiveAsync(TimeSpan.FromMilliseconds(20));
                            if (secondC2dMsg != null)
                            {
                                //put it back to the queue for the next pickup
                                _ = loraDeviceInfo.HubSender.AbandonAsync(secondC2dMsg);

                                //set the fpending flag so the lora device will call us back for the next message
                                fctl = new byte[1] { 48 };

                            }

                            bytesC2dMsg = c2dMsg.GetBytes();
                            fport = new byte[1] { 1 };

                            if (bytesC2dMsg != null)
                                Console.WriteLine($"C2D message: {Encoding.UTF8.GetString(bytesC2dMsg)}");

                            //todo ronnie implement a better max payload size by datarate
                            //cut to the max payload of lora for any EU datarate
                            if(bytesC2dMsg.Length>51)
                                Array.Resize(ref bytesC2dMsg, 51);

                            Array.Reverse(bytesC2dMsg);
                        }

                     
                        //if confirmation or cloud to device msg send down the message
                        if (loraMessage.loRaMessageType == LoRaMessageType.ConfirmedDataUp || c2dMsg != null)
                        {
                            //check if we are not too late for the 1 and 2 window
                            if (((DateTime.UtcNow - startTimeProcessing) <= TimeSpan.FromMilliseconds(1900)))
                            {

                                //increase the fcnt down and save it to iot hub twins
                                loraDeviceInfo.FCntDown++;

                                Console.WriteLine($"Down frame counter: {loraDeviceInfo.FCntDown}");


                                //Saving both fcnts to twins
                                _ = loraDeviceInfo.HubSender.UpdateFcntAsync(loraDeviceInfo.FCntUp, loraDeviceInfo.FCntDown);

                                var _datr = ((UplinkPktFwdMessage)loraMessage.loraMetadata.fullPayload).rxpk[0].datr;

                                uint _rfch = ((UplinkPktFwdMessage)loraMessage.loraMetadata.fullPayload).rxpk[0].rfch;

                                double _freq = ((UplinkPktFwdMessage)loraMessage.loraMetadata.fullPayload).rxpk[0].freq;

                                uint txDelay = 0;


                                //if we are already longer than 900 mssecond move to the 2 second window
                                //uncomment to force second windows usage
                                //Thread.Sleep(901);
                                if ((DateTime.UtcNow - startTimeProcessing) > TimeSpan.FromMilliseconds(900) )
                                {
                                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RX2_DATR")))
                                    {
                                        Console.WriteLine("using standard second receive windows");

                                        //using EU fix DR for RX2
                                        _freq = 869.525;
                                        _datr = "SF12BW125";


                                    }
                                    //if specific twins are set, specify second channel to be as specified
                                    else
                                    {
                                        
                                        _freq = double.Parse(Environment.GetEnvironmentVariable("RX2_FREQ"));
                                        _datr = Environment.GetEnvironmentVariable("RX2_DATR");
                                        Console.WriteLine("using specific second receive windows freq : {0}, datr:{1}", _freq,_datr);

                                    }

                                    txDelay = 1000000;
                                }


                                long _tmst = ((UplinkPktFwdMessage)loraMessage.loraMetadata.fullPayload).rxpk[0].tmst + txDelay;


                                Byte[] devAddrCorrect = new byte[4];
                                Array.Copy(loraMessage.payloadMessage.devAddr, devAddrCorrect, 4);
                                Array.Reverse(devAddrCorrect);

                                //todo mik check what is the A0
                                LoRaPayloadStandardData ackLoRaMessage = new LoRaPayloadStandardData(StringToByteArray("A0"),
                                    devAddrCorrect,                                   
                                    fctl,
                                    BitConverter.GetBytes(loraDeviceInfo.FCntDown),
                                    null,
                                    fport,
                                    bytesC2dMsg,
                                    1);


                                ackLoRaMessage.PerformEncryption(loraDeviceInfo.AppSKey);
                                ackLoRaMessage.SetMic(loraDeviceInfo.NwkSKey);



                                byte[] rndToken = new byte[2];
                                Random rnd = new Random();
                                rnd.NextBytes(rndToken);

                                //todo ronnie should check the device twin preference if using confirmed or unconfirmed down
                                LoRaMessage ackMessage = new LoRaMessage(ackLoRaMessage, LoRaMessageType.ConfirmedDataDown, rndToken, _datr, 0, _freq, _tmst);

                                udpMsgForPktForwarder = ackMessage.physicalPayload.GetMessage();


                                //confirm the message to iot hub only if we are in time for a delivery
                                if (c2dMsg != null)
                                {
                                    _ = loraDeviceInfo.HubSender.CompleteAsync(c2dMsg);
                                    Console.WriteLine("Complete the c2d msg to IoT Hub");
                                }

                            }
                            else
                            {
                                PhysicalPayload pushAck = new PhysicalPayload(loraMessage.physicalPayload.token, PhysicalIdentifier.PUSH_ACK, null);
                                udpMsgForPktForwarder = pushAck.GetMessage();


                                _ = loraDeviceInfo.HubSender.UpdateFcntAsync(loraDeviceInfo.FCntUp, null);


                                //put back the c2d message to the queue for the next round
                                _ = loraDeviceInfo.HubSender.AbandonAsync(c2dMsg);

                                Console.WriteLine("Too late for down message, sending only ACK to gateway");
                            }


                        }
                        //No ack requested and no c2d message we send the udp ack only to the gateway
                        else if (loraMessage.loRaMessageType == LoRaMessageType.UnconfirmedDataUp && c2dMsg == null)
                        {

                            PhysicalPayload pushAck = new PhysicalPayload(loraMessage.physicalPayload.token, PhysicalIdentifier.PUSH_ACK, null);
                            udpMsgForPktForwarder = pushAck.GetMessage();

                            _ = loraDeviceInfo.HubSender.UpdateFcntAsync(loraDeviceInfo.FCntUp, null);

                        }

                    }
                    else
                    {
                        Console.WriteLine("Check MIC failed! Device will be ignored from now on...");
                        loraDeviceInfo.IsOurDevice = false;
                    }
                }
                else
                {
                    Console.WriteLine($"Ignore message because is not linked to this GatewayID");
                }
            }
            else
            {
                Console.WriteLine($"Ignore message because is not our device");
            }

            Console.WriteLine($"Processing time: {DateTime.UtcNow - startTimeProcessing}");

            return udpMsgForPktForwarder;
        }

       

        private async Task<byte[]> ProcessJoinRequest(LoRaMessage loraMessage)
        {
            Console.WriteLine("Join Request Received");

            byte[] udpMsgForPktForwarder = new Byte[0];

            LoraDeviceInfo joinLoraDeviceInfo;

            var joinReq = (LoRaPayloadJoinRequest)loraMessage.payloadMessage;

            Array.Reverse(joinReq.devEUI);
            Array.Reverse(joinReq.appEUI);

            string devEui = BitConverter.ToString(joinReq.devEUI).Replace("-", "");
            string devNonce = BitConverter.ToString(joinReq.devNonce).Replace("-", "");

            //checking if this devnonce was already processed or the deveui was already refused
            Cache.TryGetValue(devEui, out joinLoraDeviceInfo);


            //we have a join request in the cache
            if (joinLoraDeviceInfo != null)
            {

               
                //it is not our device so ingore the join
                if (!joinLoraDeviceInfo.IsOurDevice)
                {
                    Console.WriteLine("Join Request refused the device is not ours");
                    return null;
                }
                //is our device but the join was not valid
                else if (!joinLoraDeviceInfo.IsJoinValid)
                {
                    //if the devNonce is equal to the current it is a potential replay attck
                    if (joinLoraDeviceInfo.DevNonce == devNonce)
                    {
                        Console.WriteLine("Join Request refused devNonce already used");
                        return null;
                    }

                    //Check if the device is trying to join through the wrong gateway
                    if (!String.IsNullOrEmpty(joinLoraDeviceInfo.GatewayID) && joinLoraDeviceInfo.GatewayID.ToUpper() != GatewayID.ToUpper())
                    {
                        Console.WriteLine("Device trying to join not through its linked gateway");
                        return null;
                    }
                }

            }

            

            joinLoraDeviceInfo = await LoraDeviceInfoManager.PerformOTAAAsync(GatewayID, devEui, BitConverter.ToString(joinReq.appEUI).Replace("-", ""), devNonce);

            if (joinLoraDeviceInfo.IsJoinValid)
            {

                byte[] appNonce = StringToByteArray(joinLoraDeviceInfo.AppNonce);

                byte[] netId = StringToByteArray(joinLoraDeviceInfo.NetId);



                byte[] devAddr = StringToByteArray(joinLoraDeviceInfo.DevAddr);

                string appKey = joinLoraDeviceInfo.AppKey;

                Array.Reverse(netId);
                Array.Reverse(appNonce);

                LoRaPayloadJoinAccept loRaPayloadJoinAccept = new LoRaPayloadJoinAccept(
                    //NETID 0 / 1 is default test 
                    BitConverter.ToString(netId).Replace("-", ""),
                    //todo add app key management
                    appKey,
                    //todo add device address management
                    devAddr,
                    appNonce
                    );

                var _datr = ((UplinkPktFwdMessage)loraMessage.loraMetadata.fullPayload).rxpk[0].datr;

                uint _rfch = ((UplinkPktFwdMessage)loraMessage.loraMetadata.fullPayload).rxpk[0].rfch;

                double _freq = ((UplinkPktFwdMessage)loraMessage.loraMetadata.fullPayload).rxpk[0].freq;
                //set tmst for the normal case
                long _tmst = ((UplinkPktFwdMessage)loraMessage.loraMetadata.fullPayload).rxpk[0].tmst+ 5000000;

                //uncomment to force second windows usage
                //Thread.Sleep(4600-(int)(DateTime.Now - startTimeProcessing).TotalMilliseconds);
                //in this case it's too late, we need to break
                if ((DateTime.UtcNow - startTimeProcessing) > TimeSpan.FromMilliseconds(6000))
                {
                    Console.WriteLine("Processing of the join request took too long, sending no message");
                    var physicalResponse = new PhysicalPayload(loraMessage.physicalPayload.token, PhysicalIdentifier.PULL_RESP, null);
                    
                    return physicalResponse.GetMessage();
                }
                //in this case the second join windows must be used
                else if ((DateTime.UtcNow - startTimeProcessing) > TimeSpan.FromMilliseconds(4500))
                {
                    Console.WriteLine("Processing of the join request took too long, using second join accept receive windows");
                    _tmst = ((UplinkPktFwdMessage)loraMessage.loraMetadata.fullPayload).rxpk[0].tmst + 6000000;
                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RX2_DATR")))
                    {
                        Console.WriteLine("using standard second receive windows for join request");

                        //using EU fix DR for RX2
                        _freq = 869.525;
                        _datr = "SF12BW125";


                    }
                    //if specific twins are set, specify second channel to be as specified
                    else
                    {
                        Console.WriteLine("using custom second receive windows for join request");

                        _freq = double.Parse(Environment.GetEnvironmentVariable("RX2_FREQ"));
                        _datr = Environment.GetEnvironmentVariable("RX2_DATR");
                    }
                  
         
                }
              

                LoRaMessage joinAcceptMessage = new LoRaMessage(loRaPayloadJoinAccept, LoRaMessageType.JoinAccept, loraMessage.physicalPayload.token, _datr, 0, _freq, _tmst);

                udpMsgForPktForwarder = joinAcceptMessage.physicalPayload.GetMessage();


                joinLoraDeviceInfo.HubSender = new IoTHubSender(joinLoraDeviceInfo.DevEUI, joinLoraDeviceInfo.PrimaryKey);

                //open the connection to iot hub without waiting for perf optimization in case of the device start sending a msg just after join request
                _ = joinLoraDeviceInfo.HubSender.OpenAsync();

                //join request resets the frame counters
                joinLoraDeviceInfo.FCntUp = 0;
                joinLoraDeviceInfo.FCntDown = 0;

                
                //update the frame counter on the server
                _ = joinLoraDeviceInfo.HubSender.UpdateFcntAsync(joinLoraDeviceInfo.FCntUp, joinLoraDeviceInfo.FCntDown);



                //add to cache for processing normal messages. This awoids one additional call to the server.
                Cache.AddToCache(joinLoraDeviceInfo.DevAddr, joinLoraDeviceInfo);

                Console.WriteLine("Join Accept sent");
                  
             }

            //add to cache to avoid replay attack, btw server side does the check too.
            Cache.AddToCache(devEui, joinLoraDeviceInfo);

            return udpMsgForPktForwarder;
        }

        private byte[] StringToByteArray(string hex)
        {

            return Enumerable.Range(0, hex.Length)

                             .Where(x => x % 2 == 0)

                             .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))

                             .ToArray();

        }

       

        public void Dispose()
        {

        }
    }
}
