namespace LuxNode.Models
{
    public class CoinCard
    {
        public string Symbol { get; set; } = string.Empty;   // LUX, BTC, MCHIC, etc.
        public string Name { get; set; } = string.Empty;     // LuxChain, Manga Chic, Centraliser...
        public string Image { get; set; } = string.Empty;    // chemin vers l’image
        public decimal Balance { get; set; }
        public string Address { get; set; } = string.Empty;
    }
}
