using Microsoft.Playwright;
using System.Xml;

namespace ft_dca
{
    public class Robinhood
    {
        IPlaywright Pw { get; set; }
        IBrowser Browser { get; set; }
        IPage Page { get; set; }
        IBrowserContext Context { get; set; }
        readonly XmlDocument xml = new XmlDocument();
        readonly XmlElement? cfg;
        int delayAmount = 1000;
        bool hasStorageState { get { return File.Exists("state_rh.json"); } }

        Dictionary<string, decimal> priceLookup = new Dictionary<string, decimal>();
        Dictionary<string, decimal> amountLookup = new Dictionary<string, decimal>();
        Dictionary<string, decimal> gainLookup = new Dictionary<string, decimal>();
        Dictionary<string, decimal> low52Lookup = new Dictionary<string, decimal>();
        Dictionary<string, decimal> high52Lookup = new Dictionary<string, decimal>();

        public Robinhood()
        {
            //xml.Load(Environment.GetCommandLineArgs()[1]);
            xml.Load("config_rh.xml");
            cfg = xml["config"];
            bool headless = !Convert.ToBoolean(cfg?["login"]?.GetAttribute("showBrowser") ?? "true");
            Pw = Playwright.CreateAsync().GetAwaiter().GetResult();
            Browser = Pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = headless, Channel = "chrome" }).GetAwaiter().GetResult();
            if (hasStorageState)
            {
                Context = Browser.NewContextAsync(new BrowserNewContextOptions { StorageStatePath = "state_rh.json" }).GetAwaiter().GetResult();
                Page = Context.NewPageAsync().GetAwaiter().GetResult();
            }
            else
            {
                Page = Browser.NewPageAsync().GetAwaiter().GetResult();
                Context = Page.Context;
            }
        }

        public async Task RunBots()
        {
            if (cfg == null) return;

            var bots = cfg.GetElementsByTagName("bot");
            if (bots == null) return;
            foreach (XmlElement bot in bots)
            {
                var symbol = bot.GetAttribute("symbol");
                decimal startPrice = Convert.ToDecimal(bot.GetAttribute("startPrice"));
                await UpdateDictionaries(symbol);
                var price = priceLookup[symbol];
                var amount = amountLookup[symbol];
                var amount2 = amount; //amount2 refers to the amount as if it stayed constant at the start price  (except when useStartPriceAmounts=false)

                //amount is currently relative to the current price but we want amount relative to the startPrice if useStartPriceAmounts="true"
                bool useStartPriceAmounts = false;
                if (bot.HasAttribute("useStartPriceAmounts") && bot.GetAttribute("useStartPriceAmounts") == "true")
                {
                    useStartPriceAmounts = true;
                    decimal shareCount = amount / price;
                    amount2 = startPrice * shareCount; 
                }

                var gain = gainLookup[symbol];
                var low52 = low52Lookup[symbol];
                var high52 = high52Lookup[symbol];

                Console.WriteLine($"{symbol} total gain/loss: {gain}%, value ${amount}");

                //Sell if percentTakeProfit attribute exists and gain is greater than this value
                if (bot.HasAttribute("percentTakeProfit"))
                {
                    decimal percentTakeProfit = Convert.ToDecimal(bot.GetAttribute("percentTakeProfit"));
                    if (gain >= percentTakeProfit)
                    {
                        var amountToHold = (bot.HasAttribute("amountToHold")) ? Convert.ToDecimal(bot.GetAttribute("amountToHold")) : 0;

                        //if there's at least $1 worth selling not in the amount to hold
                        if (amount > amountToHold && (amount - amountToHold) > 1)
                        {

                            Console.WriteLine($"--Placing SELL order for {amount - amountToHold} share(s) of {symbol} at ${price}/share because total gain is greater than {percentTakeProfit}%");
                            //in practice it's probably safer to hold some in order to avoid it asking if you want to sell everything but we'll figure that out when we get there
                            await Sell(symbol, amount - amountToHold);
                        }
                    }
                }

                //Skip DCA if the price is too high relative to the 52 week range(0 - 100 % where lower is more oversold--protective measure)
                if (bot.HasAttribute("percent52WeekBelow"))
                {
                    var percent52Week = 100 * (price - low52) / (high52 - low52); // the percentage the last price is at within that 52 week window
                    decimal percent52WeekBelow = Convert.ToDecimal(bot.GetAttribute("percent52WeekBelow"));
                    if (percent52Week > percent52WeekBelow)
                    {
                        Console.WriteLine($"Skipping {symbol} because percent52Week>percent52WeekBelow: {percent52Week.ToString("#.#")}>{percent52WeekBelow.ToString("#.##")}\n");
                        continue;
                    }
                    else Console.WriteLine($"{symbol} 52 week percentage is {percent52Week.ToString("#.#")}%");
                }

                //Based on the amount we have we need to figure out where we are at in the buy schedule
                var buys = bot?.GetElementsByTagName("buy");
                if (buys == null) continue;

                int index = 0;
                foreach (XmlElement buy in buys)
                {
                    var partialAmount = Convert.ToDecimal(buy.GetAttribute("amount"));
                    var percentDrop = Convert.ToDecimal(buy.GetAttribute("percentDrop"));
                    amount2 -= partialAmount; //so in this case we may be subtracting in terms of startPrice dollars
                    if (amount2 < 0)
                    {
                        amount2 = -amount2;
                        //to be consistent we should also buy in startPrice dollars I am thinking
                        if (useStartPriceAmounts)
                        {
                            decimal shareCount = amount2 / price;
                            amount2 = startPrice * shareCount;
                        }
                        Console.WriteLine($"{symbol} is at buy index {index}");
                        decimal buyPrice = startPrice * .01m * (100 - percentDrop);
                        if (price < buyPrice)
                        {
                            Console.WriteLine($"{symbol} purchase condition met: price<buyPrice: {price.ToString("#.##")}<{buyPrice.ToString("#.##")}");
                            Console.WriteLine($"--Placing BUY order for ${amount2.ToString("#.##")} of {symbol} for ${price.ToString("#.##")}/share because purchase condition at buy index {index} was met");
                            await Buy(symbol, amount2);
                        }
                        else Console.WriteLine($"{symbol} purchase condition NOT met: lastPrice<buyPrice is false: {price.ToString("#.##")}<{buyPrice.ToString("#.##")}");
                        break;
                    }
                    index++;
                }

                await Task.Delay(delayAmount * 5); //lets not piss off RH too much
                Console.WriteLine();
            }
        }

        public async Task UpdateDictionaries(string symbol)
        {
            await Page.GotoAsync($"https://robinhood.com/stocks/{symbol}");

            var price = (await Page.TextContentAsync("div#sdp-market-price") ?? decimal.MaxValue.ToString()).ToString();
            var trash = price.IndexOf("-$,");
            if(trash != -1) price = price.Substring(1, trash - 1);
            priceLookup.Set(symbol, price);

            var el = await WaitFor("div.grid-2 div:text('Your market value') + h2");
            string amount = "0";

            //amount = (await Page.TextContentAsync("div.grid-2 div:text('Your market value') + h2")).ToString().Substring(1);
            if (el != null) amount = (await el.TextContentAsync() ?? "0").ToString().Substring(1);
            amountLookup.Set(symbol, amount);

            var trs = await Page.QuerySelectorAllAsync("div.grid-2 table.table tbody tr");
            if (trs.Count > 1)
            {
                var tds = await trs[1].QuerySelectorAllAsync("td");
                if (tds.Count > 2)
                {
                    var gain = await tds[2].TextContentAsync();
                    if (gain == null) return;
                    var start = gain.IndexOf("(") + 1;
                    var end = gain.IndexOf("%)");
                    gain = gain.Substring(start, end - start);
                    gainLookup.Set(symbol, gain);
                }
            }
            else gainLookup.Set(symbol, "0");

            var high52 = (await Page.TextContentAsync("div#sdp-stats-52-week-high") ?? decimal.MaxValue.ToString()).ToString();
            high52 = high52.Substring(high52.IndexOf("$") + 1);
            high52Lookup.Set(symbol, high52);

            var low52 = (await Page.TextContentAsync("div#sdp-stats-52-week-low") ?? "0").ToString();
            low52 = low52.Substring(low52.IndexOf("$") + 1);
            low52Lookup.Set(symbol, low52);

        }

        //the first time you will have to solve a puzzle and verify your phone number (set showBrowser=true the first time)
        public async Task Login()
        {
            if (cfg == null) return;
            Console.WriteLine("Logging into Robinhood using credentials from <login>");
            await Page.GotoAsync("https://robinhood.com/login");
            //await Page.WaitForLoadStateAsync();
            //await Task.Delay(delayAmount);

            await Page.FillAsync("input[name='username']", cfg["login"]?.GetAttribute("userName") ?? "");
            await Page.FillAsync("input[name='password']", cfg["login"]?.GetAttribute("password") ?? "");
            await Page.ClickAsync("div[id='submitbutton'] button");
            //await Task.Delay(delayAmount);
            await Page.WaitForURLAsync("https://robinhood.com/", new PageWaitForURLOptions() { Timeout = 1000 * 60 * 3 }); //may take few minutes to get here
            await Page.Context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = "state_rh.json" });
            Console.WriteLine("Login Successful");
            Console.WriteLine();
        }

        //WaitForSelectorAsync doesn't seem to do what it needs to
        public async Task<IElementHandle?> WaitFor(string selector, int seconds = 5)
        {
            IElementHandle? el;
            int count = 0;
            bool elIsNull = true;
            do
            {
                el = await Page.QuerySelectorAsync(selector);
                elIsNull = (el == null);
                if (elIsNull) await Task.Delay(125); //1000/8 = 125 and 1000>>3 = 125
                count++;
                if (count >> 3 >= seconds) break;
            } while (elIsNull);

            return el;
        }

        //buy at market in dollars
        public async Task Buy(string symbol, decimal amount)
        {
            await Page.GotoAsync($"https://robinhood.com/stocks/{symbol}");
            await Page.FillAsync("input[name='dollarAmount']", amount.ToString("#.##"));
            await Page.ClickAsync("button[type='submit']");
            var btn = await WaitFor("span:text('Review Order')");
            if (btn == null) return;
            await btn.ClickAsync();
            btn = await WaitFor("span:text('Buy')");
            if (btn != null)
            {
                await btn.ClickAsync(new ElementHandleClickOptions() { Force = true });
                Console.WriteLine($"{symbol} BUY order in amount of ${amount.ToString("#.##")} - Success!");
            }
            else Console.WriteLine($"{symbol} BUY order in amount of ${amount.ToString("#.##")} - Failure! - Markets are closed or attempted daytrade");
            //{
            //    //you could queue the order but probably doesn't really make sense to do that
            //    btn = await Page.QuerySelectorAsync("span:text('Queue dollars-based order')");
            //    if (btn != null)
            //    {
            //        await btn.ClickAsync();
            //        btn = await WaitFor("span:text('Buy')");
            //        if (btn != null) await btn.ClickAsync();
            //    }
            //}
        }

        //sell at market in dollars
        public async Task Sell(string symbol, decimal amount)
        {
            await Page.GotoAsync($"https://robinhood.com/stocks/{symbol}");
            await Page.ClickAsync($"span:text('Sell {symbol}')");
            await Page.FillAsync("input[name='dollarAmount']", amount.ToString("#.##"));
            await Page.ClickAsync("button[type='submit']");
            var btn = await WaitFor("span:text('Review Order')");
            if (btn == null) return;
            await btn.ClickAsync();
            btn = await WaitFor("span:text('Sell')");
            if (btn != null) {
                await btn.ClickAsync(new ElementHandleClickOptions() { Force = true });
                Console.WriteLine($"{symbol} SELL order in amount of ${amount.ToString("#.##")} - Success!");
            }
            else Console.WriteLine($"{symbol} SELL order in amount of ${amount.ToString("#.##")} - Failure! - Markets are closed or attempted daytrade");
        }
    }

    public static class Extensions
    {
        public static void Set(this Dictionary<string, decimal> dic, string key, string value)
        {
            if (key == null || value == null) return;
            if (dic.ContainsKey(key)) dic[key] = Convert.ToDecimal(value);
            else dic.Add(key, Convert.ToDecimal(value));
        }
    }
}
