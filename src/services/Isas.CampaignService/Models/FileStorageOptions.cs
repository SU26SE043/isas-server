namespace Isas.CampaignService.Models
{
    public class FileStorageOptions
    {
        public string ServiceURL { get; set; } = default!;
        public string AccessKey { get; set; } = default!;
        public string SecretKey { get; set; } = default!;
        public string BucketName { get; set; } = "isas-employer-files";
        public bool ForcePathStyle { get; set; } = true;
    }
}
