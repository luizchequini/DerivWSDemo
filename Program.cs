using Newtonsoft.Json.Linq;
using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DerivWSDemo
{
    class Program
    {
        private ClientWebSocket ws = new ClientWebSocket();

        private string app_id = "27947"; // Change this to yor app_id. Get it from here https://developers.deriv.com/docs/app-registration/
        public static string token = "GCSHzEGz3P5g3v2"; //Change this to your token. Get it from here https://app.deriv.com/account/api-token
        private string websocket_url = "wss://ws.binaryws.com/websockets/v3?app_id=";

        public async Task SendRequest(string data)
        {

            while (this.ws.State == WebSocketState.Connecting) { };
            if (this.ws.State != WebSocketState.Open)
            {
                throw new Exception("Connection is not open.");
            }

            var reqAsBytes = Encoding.UTF8.GetBytes(data);
            ArraySegment<byte> ticksRequest = new(reqAsBytes);

            await this.ws.SendAsync(ticksRequest,
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

            Console.WriteLine("The request has been sent: ");
            Console.WriteLine(data);
            Console.WriteLine("\r\n \r\n");


        }

        public async Task StartListen(int amount, string basis, string contract_type, string currency, int duration, string duration_unit, string symbol, float price)
        {
            WebSocketReceiveResult result;

            int loss = 1;
            while (this.ws.State == WebSocketState.Open)
            {
                ArraySegment<byte> buffer = new(new byte[4096]);
                result = await this.ws.ReceiveAsync(new ArraySegment<byte>(buffer.Array), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("Connection Closed!");
                    break;
                }
                else
                {
                    var str = Encoding.UTF8.GetString(buffer.Array, 0, result.Count);
                    // Console.WriteLine(str); //uncomment to see full json response
                    JObject resultObject = JObject.Parse(str);
                    /*
					* Since we can not ensure calls to websocket are made in order we must wait for
					* the response to the authorize call before sending the buy request.
					*/
                    if ((resultObject["error"] != null))
                    {
                        Console.WriteLine(resultObject["error"]["code"]);
                        Console.WriteLine(resultObject["error"]["message"]);
                    }
                    else if (string.Equals((string)resultObject["msg_type"], "authorize"))
                    {
                        var russianStrategy = await RussianStrategy(symbol);
                        string parameters = "";
                        string data;

                        if (russianStrategy.Equals("CALL"))
                        {
                            Console.WriteLine("C O M P R A {0}", price);
                            contract_type = russianStrategy;
                            parameters = " \"parameters\": { \"amount\":" + amount + ", \"basis\": \"" + basis + "\", \"contract_type\": \"" + contract_type + "\", \"currency\": \"" + currency + "\", \"duration\": " + duration + ", \"duration_unit\": \"" + duration_unit + "\", \"symbol\": \"" + symbol + "\" }}";
                        }
                        else if (russianStrategy.Equals("PUT"))
                        {
                            Console.WriteLine("V E N D A {0}", price);
                            contract_type = russianStrategy;
                            parameters = " \"parameters\": { \"amount\":" + amount + ", \"basis\": \"" + basis + "\", \"contract_type\": \"" + contract_type + "\", \"currency\": \"" + currency + "\", \"duration\": " + duration + ", \"duration_unit\": \"" + duration_unit + "\", \"symbol\": \"" + symbol + "\" }}";
                        }
                        else
                        {
                            Console.WriteLine("D E S C O N E C T E D");
                        }

                        data = "{\"buy\": 1, \"subscribe\": 1, \"price\": " + price + "," + parameters;
                        this.SendRequest(data).Wait();
                    }
                    else if (string.Equals((string)resultObject["msg_type"], "buy"))
                    {
                        Console.WriteLine("contract Id {0}", resultObject["buy"]["contract_id"]);
                        Console.WriteLine("Details {0}", resultObject["buy"]["longcode"]);
                    }
                    else if (string.Equals((string)resultObject["msg_type"], "proposal_open_contract"))
                    { //Because we subscribed to the buy request we will receive updates on our open contract.
                        bool isSold = (bool)resultObject["proposal_open_contract"]["is_sold"];
                        if (isSold)
                        { //if `isSold` is true it means our contract has finished and we can see if we won or not.
                            Console.WriteLine("Contract {0}", resultObject["proposal_open_contract"]["status"]);
                            Console.WriteLine("Profit {0}", resultObject["proposal_open_contract"]["profit"]);

                            string wonOrLoss = resultObject["proposal_open_contract"]["status"].ToString();

                            string data = "{ \"authorize\": \"" + token + "\"}";

                            SendRequest(data).Wait();
                            
                            if (wonOrLoss.Contains("won"))
                            {
                                price = price / loss;
                                loss = 1;
                                StartListen(amount, basis, contract_type, currency, duration, duration_unit, symbol, price).Wait();
                            }
                            else if (wonOrLoss.Contains("lost"))
                            {
                                loss *= 2;
                                price = price * loss;
                                StartListen(amount, basis, contract_type, currency, duration, duration_unit, symbol, price).Wait();
                            }

                            //ws.Abort();
                            //ws.Dispose();
                        }
                        else
                        { // we can track the status of our contract as updates to the spot price occur.
                            float currentSpot = (float)resultObject["proposal_open_contract"]["current_spot"];
                            float entrySpot = 0;
                            if (!String.IsNullOrEmpty((string)resultObject["proposal_open_contract"]["entry_tick"]))
                            {
                                entrySpot = (float)resultObject["proposal_open_contract"]["entry_tick"];
                            }
                            Console.WriteLine("Entry spot {0}", entrySpot);
                            Console.WriteLine("Current spot {0}", currentSpot);
                            Console.WriteLine("Difference {0}", (currentSpot - entrySpot));
                            Console.WriteLine("Price {0}", price);
                            Console.WriteLine("\r\n");
                        }
                    }
                }
            }
        }

        public async Task<string> RussianStrategy(string symbol)
        {
            float entrySpot = 0;
            int contUp = 0;
            int contDonw = 0;

            WebSocketReceiveResult result;
            while (this.ws.State == WebSocketState.Open)
            {
                var buffer = new ArraySegment<byte>(new byte[1024]);
                do
                {
                    result = await this.ws.ReceiveAsync(new ArraySegment<byte>(buffer.Array), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine("Connection Closed!");
                        break;
                    }
                    else
                    {
                        var str = Encoding.UTF8.GetString(buffer.Array, 0, result.Count);
                        JObject resultObject = JObject.Parse(str);

                        Console.WriteLine("Received Data at: " + DateTime.Now);
                        Console.WriteLine(resultObject["tick"]["quote"]);
                        Console.WriteLine("\r\n");

                        float currentSpot = (float)resultObject["tick"]["quote"];

                        if (entrySpot == 0)
                        {
                            entrySpot = currentSpot;
                        }

                        Console.WriteLine("entrySpot: " + entrySpot);
                        Console.WriteLine("currentSpot: " + currentSpot);
                        Console.WriteLine("contUp: " + contUp);
                        Console.WriteLine("contDonw: " + contDonw);

                        if (entrySpot >= currentSpot)
                        {
                            contUp++;
                            contDonw = 0;

                            if (contUp == 3)
                            {
                                Console.WriteLine("contUp: " + contUp);
                                Console.WriteLine("contDonw: " + contDonw);
                                contUp = 0;

                                return "CALL";
                            }
                        }
                        else
                        {
                            contDonw++;
                            contUp = 0;

                            if (contDonw == 3)
                            {
                                Console.WriteLine("contUp: " + contUp);
                                Console.WriteLine("contDonw: " + contDonw);
                                contDonw = 0;

                                return "PUT";
                            }
                        }
                    }

                } while (!result.EndOfMessage);
            }

            return "desconected";
        }

        public async Task Connect()
        {

            Uri uri = new Uri(websocket_url + app_id);
            Console.WriteLine("Prepare to connect to: " + uri.ToString());
            Console.WriteLine("\r\n");
            //WebProxy proxyObject = new WebProxy("http://172.30.160.1:1090",true); //these 2 lines allow proxying set the proxy url as needed
            //ws.Options.Proxy = proxyObject;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            await ws.ConnectAsync(uri, CancellationToken.None);

            Console.WriteLine("The connection is established!");
            Console.WriteLine("\r\n");
        }

        static void Main(string[] args)
        {
            var bws = new Program();
            bws.Connect().Wait();


            int amount = 10;
            string basis = "stake";
            string contract_type = "CALL";
            string currency = "USD";
            int duration = 5;
            string duration_unit = "t";
            string symbol = "R_10";
            float price = 10;

            string data = "{\"ticks\":\"" + symbol + "\"}";
            bws.SendRequest(data).Wait();

            string data1 = "{ \"authorize\": \"" + token + "\"}";
            bws.SendRequest(data1).Wait();

            bws.StartListen(amount, basis, contract_type, currency, duration, duration_unit, symbol, price).Wait();

        }
    }
}
