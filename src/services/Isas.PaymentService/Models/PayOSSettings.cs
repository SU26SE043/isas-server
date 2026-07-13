namespace Isas.PaymentService.Models
{
    public class PayOSSettings
    {
        public string ClientId { get; set; } = null!;
        public string ApiKey { get; set; } = null!;
        public string ChecksumKey { get; set; } = null!; 
        public string ReturnUrl { get; set; } = null!;  // add these
        public string CancelUrl { get; set; } = null!;
    }
}
