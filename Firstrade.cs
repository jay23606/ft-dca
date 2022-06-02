using Microsoft.Playwright;
using System.Xml;

namespace ft_dca
{
    public class Firstrade
    {
        IPlaywright Pw { get; set; }
        IBrowser Browser { get; set; }
        IPage Page { get; set; }
        IBrowserContext Context { get; set; }

        readonly XmlDocument xml = new XmlDocument();
        readonly XmlElement? cfg;
        int delayAmount = 2000;

        Dictionary<string, decimal> lastPriceLookup = new Dictionary<string, decimal>();
        Dictionary<string, int> quantityLookup = new Dictionary<string, int>();
        Dictionary<string, decimal> gainLookup = new Dictionary<string, decimal>();
        Dictionary<string, decimal> valueLookup = new Dictionary<string, decimal>();
        Dictionary<string, decimal> low52Lookup = new Dictionary<string, decimal>();
        Dictionary<string, decimal> high52Lookup = new Dictionary<string, decimal>();
        bool hasStorageState { get { return File.Exists("state.json"); } }

        public Firstrade()
        {
            xml.Load(Environment.GetCommandLineArgs()[1]);
            cfg = xml["config"];
            bool headless = !Convert.ToBoolean(cfg?["login"]?.GetAttribute("showBrowser") ?? "true");
            Pw = Playwright.CreateAsync().GetAwaiter().GetResult();
            Browser = Pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = headless, Channel = "chrome" }).GetAwaiter().GetResult();

            if (hasStorageState)
            {
                Context = Browser.NewContextAsync(new BrowserNewContextOptions { StorageStatePath = "state.json" }).GetAwaiter().GetResult();
                Page = Context.NewPageAsync().GetAwaiter().GetResult();
            }
            else
            {
                Page = Browser.NewPageAsync().GetAwaiter().GetResult();
                Context = Page.Context;
            }
        }

        public async Task Login()
        {
            if (cfg == null) return;
            Console.WriteLine("Logging into Firstrade using credentials from <login>");
            try
            {
                await Page.GotoAsync("https://www.firstrade.com/content/en-us/welcome");
                await Task.Delay(delayAmount);

                await Page.FillAsync("input[name='username']", cfg["login"]?.GetAttribute("userName") ?? "");
                await Page.FillAsync("input[name='password']", cfg["login"]?.GetAttribute("password") ?? "");
                await Page.ClickAsync("button[id='submit']");
                await Task.Delay(delayAmount);

                if (Page.Url.Contains("enter_pin"))
                {
                    string[] PIN = (cfg["login"]?.GetAttribute("pin") ?? "").Split(',');
                    foreach (var digit in PIN)
                        await Page.ClickAsync($"div[id='{digit.Trim()}']");
                    await Page.ClickAsync("div[id='submit']");
                    await Task.Delay(delayAmount);
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

            await Page.Context.StorageStateAsync(new BrowserContextStorageStateOptions{Path = "state.json"});

            Console.WriteLine();
        }

        //orderType: B=Buy, S=Sell, SS=Sell Short, BC=Buy to Cover
        public async Task Order(string orderType, string symbol, int shares, string type = "Market", string limitPrice = "", string duration = "Day")
        {
            await CheckSession();

            try
            {
                await Page.GotoAsync("https://invest.firstrade.com/cgi-bin/main#/cgi-bin/stock_order");
                await Task.Delay(delayAmount);
                await Page.SelectOptionAsync("select[name='duration']", new SelectOptionValue { Label = duration });
                await Page.SelectOptionAsync("select[name='priceType']", new SelectOptionValue { Label = type });
                await Page.CheckAsync($"input[value='{orderType}']");
                await Page.FillAsync("input[name='symbol']", symbol);
                await Page.FillAsync("input[name='quantity']", shares.ToString());
                if (limitPrice != "") await Page.FillAsync("input[name='limitPrice']", limitPrice);
                await Task.Delay(delayAmount);
                await Page.ClickAsync("a[name='submitOrder']");

                var err = await Page.QuerySelectorAllAsync("div.inbox");
                if (err.Count > 0) Console.WriteLine(await err[0].TextContentAsync());
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

        //sometimes the table doesn't appear so we prefer dictionary
        public async Task<decimal?> GetLastPrice(string symbol)
        {
            await CheckSession();

            if (lastPriceLookup.ContainsKey(symbol)) return lastPriceLookup[symbol];

            try
            {
                //await Page.GotoAsync("https://invest.firstrade.com/cgi-bin/main#/cgi-bin/stock_order");
                await Page.GotoAsync("https://invest.firstrade.com/cgi-bin/main#/content/researchtools/alerts/");
                await Task.Delay(delayAmount);
                await Page.FillAsync("input[name='symbol']", symbol);
                await Page.Locator("input[name='symbol'] >> nth=0").EvaluateAsync("e => e.blur()");
                await Task.Delay(delayAmount);

                var rows = await Page.QuerySelectorAllAsync("div.condition_stock_quote_table table tbody tr");
                if (rows.Count == 0)
                {
                    //if (lastPriceLookup.ContainsKey(symbol)) return lastPriceLookup[symbol];
                    return decimal.MaxValue;
                }
                var cols = await rows[1].QuerySelectorAllAsync("td");
                if (cols.Count == 0) return decimal.MaxValue;
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


        public async Task UpdateDictionaries()
        {
            await CheckSession();
            try
            {
                await Page.GotoAsync("https://invest.firstrade.com/cgi-bin/main#/cgi-bin/acctpositions");
                await Task.Delay(delayAmount);

                var rows = await Page.QuerySelectorAllAsync("table#positiontable tbody tr");
                foreach (var row in rows)
                {
                    var cols = await row.QuerySelectorAllAsync("td");
                    //ensure first column is Symbol and second column is Quantity for this to work
                    var sym = (await cols[0].TextContentAsync() ?? "").Trim();

                    //Make sure your columns under positions are oriented like this or similar:
                    //Symbol, Quantity, Day Change($), Gain/Loss($), Change(%), Gain/Loss(%). 52 Week Low, 52 Week High, Last, Unit Cost, Market Value, Day Low

                    //Quantity
                    if (quantityLookup.ContainsKey(sym)) quantityLookup[sym] = Convert.ToInt32(await cols[1].TextContentAsync());
                    else quantityLookup.Add(sym, Convert.ToInt32(await cols[1].TextContentAsync()));

                    //Gain/Loss(%)
                    if (gainLookup.ContainsKey(sym)) gainLookup[sym] = Convert.ToDecimal(await cols[5].TextContentAsync());
                    else gainLookup.Add(sym, Convert.ToDecimal(await cols[5].TextContentAsync()));

                    //52 Week Low
                    if (low52Lookup.ContainsKey(sym)) low52Lookup[sym] = Convert.ToDecimal(await cols[6].TextContentAsync());
                    else low52Lookup.Add(sym, Convert.ToDecimal(await cols[6].TextContentAsync()));

                    //52 Week High
                    if (high52Lookup.ContainsKey(sym)) high52Lookup[sym] = Convert.ToDecimal(await cols[7].TextContentAsync());
                    else high52Lookup.Add(sym, Convert.ToDecimal(await cols[7].TextContentAsync()));

                    //Last
                    if (lastPriceLookup.ContainsKey(sym)) lastPriceLookup[sym] = Convert.ToDecimal(await cols[8].TextContentAsync());
                    else lastPriceLookup.Add(sym, Convert.ToDecimal(await cols[8].TextContentAsync()));

                    //Market Value,
                    if (valueLookup.ContainsKey(sym)) valueLookup[sym] = Convert.ToDecimal(await cols[10].TextContentAsync());
                    else valueLookup.Add(sym, Convert.ToDecimal(await cols[10].TextContentAsync()));

                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("UpdateDictionaries exception:");
                Console.WriteLine("\n" + ex.Message);
                Console.WriteLine("\n" + ex.InnerException);
                Console.WriteLine("\n" + ex.StackTrace);
                Environment.Exit(1);
            }
        }

        public async Task<int> GetShareQuantity(string symbol)
        {
            await CheckSession();
            //await UpdateDictionaries();
            if (quantityLookup.ContainsKey(symbol)) return quantityLookup[symbol];
            else return 0;
        }

        //if there is already a buy limit order than we can skip this symbol
        public async Task<bool> HasBuyLimitOrder(string symbol)
        {
            await CheckSession();

            try
            {
                symbol = symbol.ToUpper();
                await Page.GotoAsync("https://invest.firstrade.com/cgi-bin/main#/cgi-bin/orderstatus");
                await Task.Delay(delayAmount);
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
                await Login();
                return;
            }
        }

        public async Task RunBots(decimal limitBuyThreshold = .99m)
        {
            if (cfg == null) return;

            await CheckSession();
            var bots = cfg.GetElementsByTagName("bot");
            if (bots == null) return;
            foreach(XmlElement bot in bots)
            {
                var symbol = bot.GetAttribute("symbol");
                decimal startPrice = Convert.ToDecimal(bot.GetAttribute("startPrice"));
                await UpdateDictionaries();

                if (gainLookup.ContainsKey(symbol))
                {
                    Console.WriteLine($"{symbol} total gain/loss: {gainLookup[symbol]}%, value ${valueLookup[symbol]}");

                    if (bot.HasAttribute("percentTakeProfit"))
                    {
                        decimal percentTakeProfit = Convert.ToDecimal(bot.GetAttribute("percentTakeProfit"));

                        if (gainLookup[symbol]>= percentTakeProfit)
                        {
                            int shares = quantityLookup[symbol];

                            var sharesToHold = (bot.HasAttribute("sharesToHold")) ? Convert.ToInt32(bot.GetAttribute("sharesToHold")) : 0;

                            if (shares > sharesToHold)
                            {
                                var lastPrice = lastPriceLookup[symbol];
                                Console.WriteLine($"--Placing SELL order for {shares - sharesToHold} share(s) of {symbol} at ${lastPrice}/share because total gain is greater than {percentTakeProfit}%");
                                if (lastPrice > 1) 
                                    await Order("S", symbol, shares - sharesToHold, "Limit", lastPrice.ToString("#.##"), "Day+EXT");
                                else 
                                    await Order("S", symbol, shares - sharesToHold, "Limit", lastPrice.ToString("#.####"), "Day+EXT");
                            }
                        }
                    }

                    if (bot.HasAttribute("percent52WeekBelow"))
                    {
                        var low52 = low52Lookup[symbol];
                        var high52 = high52Lookup[symbol];
                        var last = lastPriceLookup[symbol];
                        var percent52Week = 100 * (last - low52) / (high52 - low52); // the percentage the last price is at within that 52 week window

                        decimal percent52WeekBelow = Convert.ToDecimal(bot.GetAttribute("percent52WeekBelow"));

                        if (percent52Week > percent52WeekBelow)
                        {
                            Console.WriteLine($"Skipping {symbol} because percent52Week>percent52WeekBelow: {percent52Week.ToString("#.#")}>{percent52WeekBelow}\n");
                            continue;
                        } 
                        else Console.WriteLine($"{symbol} 52 week percentage is {percent52Week.ToString("#.#")}%");

                    }
                }

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
                            Console.WriteLine($"{symbol} is at buy index {index}");
                            //now if we are within 1% of the limit price for this buy then we can place the order
                            var lastPrice = await GetLastPrice(symbol);
                            if (lastPrice == null) break;

                            decimal buyPrice = limitBuyThreshold * startPrice * .01m * (100 - percentDrop);

                            decimal limitPrice = startPrice * .01m * (100 - percentDrop);
                            if (lastPrice < limitPrice) limitPrice = (decimal)lastPrice;

                            if (lastPrice < buyPrice)
                            {
                                Console.WriteLine($"{symbol} purchase condition met: lastPrice<buyPrice: {lastPrice}<{buyPrice}");
                                Console.WriteLine($"--Placing BUY order for {-quantity} share(s) of {symbol} for ${limitPrice}/share because purchase condition at buy index {index} was met");

                                if (limitPrice < 1)
                                    await Order("B", symbol, -quantity, "Limit", limitPrice.ToString("#.####"), "Day+EXT");
                                else
                                    await Order("B", symbol, -quantity, "Limit", limitPrice.ToString("#.##"), "Day+EXT");
                            }
                            else Console.WriteLine($"{symbol} purchase condition NOT met: lastPrice<buyPrice: {lastPrice}<{buyPrice}");

                            break;
                        }
                        index++;
                    }
                }
                else Console.WriteLine($"{symbol} aleady has a buy order pending");
                Console.WriteLine();
            }
        }

    }
}
