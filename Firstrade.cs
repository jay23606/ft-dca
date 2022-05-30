using Microsoft.Playwright;
using System.Xml;

namespace ft_dca
{
    public class Firstrade
    {
        IPlaywright Pw { get; set; }
        IBrowser Browser { get; set; }
        IPage Page { get; set; }

        readonly XmlDocument xml = new XmlDocument();
        readonly XmlElement? cfg;

        public Firstrade()
        {
            xml.Load(Environment.GetCommandLineArgs()[1]);
            cfg = xml["config"];
            bool headless = !Convert.ToBoolean(cfg["login"].GetAttribute("showBrowser"));
            Pw = Playwright.CreateAsync().GetAwaiter().GetResult();
            Browser = Pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = headless }).GetAwaiter().GetResult();
            Page = Browser.NewPageAsync().GetAwaiter().GetResult();
        }

        public async Task Login(bool usePin = true)
        {
            Console.WriteLine("Logging into Firstrade using credentials from <login>");
            try
            {
                await Page.GotoAsync("https://www.firstrade.com/content/en-us/welcome");
                await Task.Delay(2000);

                await Page.FillAsync("input[name='username']", cfg["login"].GetAttribute("userName"));
                await Page.FillAsync("input[name='password']", cfg["login"].GetAttribute("password"));
                await Page.ClickAsync("button[id='submit']");
                await Task.Delay(2000);

                if (usePin)
                {
                    string[] PIN = cfg["login"].GetAttribute("pin").Split(',');
                    foreach (var digit in PIN)
                        await Page.ClickAsync($"div[id='{digit.Trim()}']");
                    await Page.ClickAsync("div[id='submit']");
                    await Task.Delay(2000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Login exception:");
                Console.WriteLine("\n" + ex.Message);
                Console.WriteLine("\n" + ex.InnerException);
                Console.WriteLine("\n" + ex.StackTrace);
                Environment.Exit(1);
            }
            Console.WriteLine("Login Successful");
            Console.WriteLine();
        }

        //orderType: B=Buy, S=Sell, SS=Sell Short, BC=Buy to Cover
        public async Task Order(string orderType, string symbol, int shares, string type = "Market", string limitPrice = "", string duration = "Day")
        {
            await CheckSession();

            try
            {
                await Page.GotoAsync("https://invest.firstrade.com/cgi-bin/main#/cgi-bin/stock_order");
                await Task.Delay(2000);
                await Page.SelectOptionAsync("select[name='duration']", new SelectOptionValue { Label = duration });
                await Page.SelectOptionAsync("select[name='priceType']", new SelectOptionValue { Label = type });
                await Page.CheckAsync($"input[value='{orderType}']");
                await Page.FillAsync("input[name='symbol']", symbol);
                await Page.FillAsync("input[name='quantity']", shares.ToString());
                if (limitPrice != "") await Page.FillAsync("input[name='limitPrice']", limitPrice);
                await Task.Delay(2000);
                await Page.ClickAsync("a[name='submitOrder']");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Order exception:");
                Console.WriteLine("\n" + ex.Message);
                Console.WriteLine("\n" + ex.InnerException);
                Console.WriteLine("\n" + ex.StackTrace);
                Environment.Exit(1);
            }
        }

        public async Task<decimal?> GetLastPrice(string symbol)
        {
            await CheckSession();

            try
            {
                await Page.GotoAsync("https://invest.firstrade.com/cgi-bin/main#/cgi-bin/stock_order");
                await Task.Delay(2000);
                await Page.FillAsync("input[name='symbol']", symbol);
                await Page.Locator("input[name='symbol'] >> nth=0").EvaluateAsync("e => e.blur()");
                await Task.Delay(2000);

                //this way works but is sketchy sometimes?
                //var lastPrice = await Page.Locator("div.condition_stock_quote_table").Locator("table >> nth=0").Locator("tr >> nth=1").Locator("td >> nth=1").Locator("b").InnerHTMLAsync();

                var rows = await Page.QuerySelectorAllAsync("div.condition_stock_quote_table table tbody tr");
                var cols = await rows[1].QuerySelectorAllAsync("td");
                var lastPrice = (await cols[1].TextContentAsync() ?? "").Trim();
                return (lastPrice != "") ? Convert.ToDecimal(lastPrice) : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine("GetLastPrice exception:");
                Console.WriteLine("\n" + ex.Message);
                Console.WriteLine("\n" + ex.InnerException);
                Console.WriteLine("\n" + ex.StackTrace);
                Environment.Exit(1);
            }
            return null;
        }

        public async Task<int> GetShareQuantity(string symbol)
        {
            await CheckSession();

            try
            {
                symbol = symbol.ToUpper();
                await Page.GotoAsync("https://invest.firstrade.com/cgi-bin/main#/cgi-bin/acctpositions");
                await Task.Delay(2000);

                var rows = await Page.QuerySelectorAllAsync("table#positiontable tbody tr");
                foreach (var row in rows)
                {
                    var cols = await row.QuerySelectorAllAsync("td");
                    //ensure first column is Symbol and second column is Quantity for this to work
                    var sym = (await cols[0].TextContentAsync() ?? "").Trim();
                    if (sym == symbol) return Convert.ToInt32(await cols[1].TextContentAsync());
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("GetShareQuantity exception:");
                Console.WriteLine("\n" + ex.Message);
                Console.WriteLine("\n" + ex.InnerException);
                Console.WriteLine("\n" + ex.StackTrace);
                Environment.Exit(1);
            }
            return 0;
        }

        //if there is already a buy limit order than we can skip this symbol
        //and we only place it for the day and when within a threshold 
        public async Task<bool> HasBuyLimitOrder(string symbol)
        {
            await CheckSession();

            try
            {
                symbol = symbol.ToUpper();
                await Page.GotoAsync("https://invest.firstrade.com/cgi-bin/main#/cgi-bin/orderstatus");
                await Task.Delay(2000);
                var rows = await Page.QuerySelectorAllAsync("table#order_status tbody tr");
                foreach (var row in rows)
                {
                    var cols = await row.QuerySelectorAllAsync("td");
                    var transaction = (await cols[1].TextContentAsync() ?? "").Trim();
                    if (transaction != "Buy") continue;
                    var sym = (await cols[3].TextContentAsync() ?? "").Trim();
                    if (sym == symbol)
                    {
                        var type = (await cols[4].TextContentAsync() ?? "").Trim();
                        var status = (await cols[8].TextContentAsync() ?? "").Trim();
                        if (type == "Limit" && (status == "Pending" || status == "Placed")) return true;

                        //all of the sold/cancelled stuff is at the bottom and unecessary for this?
                        if (status.StartsWith("Sold") || status.StartsWith("Cancel")) break;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine("HasBuyLimitOrder exception:");
                Console.WriteLine("\n" + ex.Message);
                Console.WriteLine("\n" + ex.InnerException);
                Console.WriteLine("\n" + ex.StackTrace);
                Environment.Exit(1);
            }
            return true;
        }

        public async Task CheckSession()
        {
            if (Page.Url.Contains("invest.firstrade.com/cgi-bin/login"))
            {
                Console.WriteLine("Session closed because you logged in from another device. Press any key to log back in.");
                Console.ReadKey();
                await Login(false);
                return;
            }
        }

        public async Task RunBots(decimal limitBuyThreshold = .99m)
        {
            await CheckSession();

            var bots = cfg?.GetElementsByTagName("bot");
            if (bots == null) return;
            foreach(XmlElement bot in bots)
            {
                var symbol = bot.GetAttribute("symbol");
                decimal startPrice = Convert.ToDecimal(bot.GetAttribute("startPrice"));

                //if there is not already a buy limit order for this symbol
                if (!await HasBuyLimitOrder(symbol))
                {
                    int quantity = await GetShareQuantity(symbol);
                    //based on the quantity we need to figure out where we are at in the buy schedule
                    var buys = bot?.GetElementsByTagName("buy");
                    if (buys == null) continue;

                    int index = 0;
                    foreach (XmlElement buy in buys)
                    {
                        var shares = Convert.ToInt32(buy.GetAttribute("shares"));
                        var percentDrop = Convert.ToDecimal(buy.GetAttribute("percentDrop"));
                        quantity -= shares;
                        if (quantity < 0)
                        {
                            Console.WriteLine($"{symbol} bot is at buy index {index}");
                            //now if we are within 1% of the limit price for this buy then we can place the order
                            var lastPrice = await GetLastPrice(symbol);
                            if (lastPrice == null) break;

                            decimal buyPrice = limitBuyThreshold * startPrice * .01m * (100 - percentDrop);

                            decimal limitPrice = startPrice * .01m * (100 - percentDrop);
                            if (lastPrice < limitPrice) limitPrice = (decimal)lastPrice;

                            if (lastPrice <= buyPrice)
                            {
                                Console.WriteLine($"Purchase condition met:  lastPrice<=buyPrice for {symbol}: {lastPrice} <= {buyPrice}");
                                Console.WriteLine($"Placing gtc buy limit order for {-quantity} shares of {symbol} for ${limitPrice}/share");

                                if (lastPrice < 1)
                                    await Order("B", symbol, -quantity, "Limit", limitPrice.ToString("#.####"), "GT90");
                                else
                                    await Order("B", symbol, -quantity, "Limit", limitPrice.ToString("#.##"), "GT90");
                            }
                            else Console.WriteLine($"Purchase condition NOT met:  lastPrice<=buyPrice for {symbol}: {lastPrice} <= {buyPrice}");

                            break;
                        }
                        index++;
                    }
                }
                else Console.WriteLine($"{symbol} bot aleady has a buy order pending");
                Console.WriteLine();
            }
        }

    }
}
