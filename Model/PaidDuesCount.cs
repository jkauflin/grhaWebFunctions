using Newtonsoft.Json;

namespace grhaWebFunctions.Model
{
    public class PaidDuesCount
    {
        public int fy { get; set; }
        public int paidCnt { get; set; }
        public int unpaidCnt { get; set; }
        public int nonCollCnt { get; set; }
        public decimal totalDue { get; set; }
        public decimal nonCollDue { get; set; }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
