using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace grhaWebFunctions.Model
{
    public class hoa_sales
    {
        public string? id { get; set; }             // id is Parcel ID
        public string? PARID { get; set; }           
        public string? CONVNUM { get; set; }
        public string? SALEDT { get; set; }          // Partition key: /SALEDT
        public string? PRICE { get; set; }
        public string? OLDOWN { get; set; }
        public string? OWNERNAME1 { get; set; }
        public string? PARCELLOCATION { get; set; }
        public string? MAILINGNAME1 { get; set; }
        public string? MAILINGNAME2 { get; set; }
        public string? PADDR1 { get; set; }
        public string? PADDR2 { get; set; }
        public string? PADDR3 { get; set; }
        public string? CreateTimestamp { get; set; }
        public string? NotificationFlag { get; set; }
        public string? ProcessedFlag { get; set; }
        public string? LastChangedBy { get; set; }
        public DateTime LastChangedTs { get; set; }
        public string? WelcomeSent { get; set; }
        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
