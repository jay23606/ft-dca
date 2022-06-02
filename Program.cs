
namespace ft_dca
{
    class Program
    {
        async static Task Main()
        {
            //Firstrade ft = new Firstrade();
            //await ft.Login();

            //////WORKS
            ////await ft.Order("B", "SNAP", 1);

            //////WORKS
            ////var lastPrice = await ft.GetLastPrice("SNAP");

            //////WORKS
            /////await UpdateDictionaries();
            ////var quantity = await ft.GetShareQuantity("SNAP");

            //////WORKS
            ////var hasOrder = await ft.HasBuyLimitOrder("SNAP");

            //while (true)
            //{
            //    await ft.RunBots();
            //    await Task.Delay(30 * 1000);
            //}

            Robinhood rh = new Robinhood();
            await rh.Login();

            //WORKS
            //await rh.Buy("CIM", 1);

            //WORKS
            //await rh.Sell("TSLA", 1);

            //WORKS
            //await rh.UpdateDictionaries("TSLA");

            while (true)
            {
                await rh.RunBots();
                await Task.Delay(1000 * 30);
            }
        }
    }
}
