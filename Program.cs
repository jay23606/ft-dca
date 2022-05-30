
namespace ft_dca
{
    class Program
    {
        async static Task Main()
        {
            Firstrade ft = new Firstrade();
            await ft.Login();

            ////WORKS
            //await ft.Order("B", "SNAP", 1);

            ////WORKS
            //var lastPrice = await ft.GetLastPrice("SNAP");

            ////WORKS
            //var quantity = await ft.GetShareQuantity("SNAP");

            ////WORKS
            //var hasOrder = await ft.HasBuyLimitOrder("SNAP");

            while (true)
            {
                await ft.RunBots();
                await Task.Delay(29 * 1000);
            }

        }
    }
}