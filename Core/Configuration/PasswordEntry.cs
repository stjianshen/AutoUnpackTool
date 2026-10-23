using System;

namespace AutoUnpackTool.Core.Configuration
{
    public class PasswordEntry
    {
        public string Password { get; set; } = string.Empty;
        public int UsageCount { get; set; } = 0;
        public DateTime LastUsedTime { get; set; } = DateTime.MinValue;

        /// <summary>
        /// 计算密码的综合评分（用于排序）
        /// 优先级：最近使用时间 > 使用频率
        /// - 1小时内使用过：获得极高的基础分（10000分），确保排在最前
        /// - 24小时内使用过：获得高分（5000分）
        /// - 7天内使用过：获得中等分（1000分）
        /// - 超过7天：仅按使用次数排序
        /// </summary>
        public double Score
        {
            get
            {
                double recencyBonus = 0;

                // 根据最近使用时间给予不同的基础加分
                if (LastUsedTime != DateTime.MinValue)
                {
                    double hoursSinceLastUse = (DateTime.Now - LastUsedTime).TotalHours;

                    if (hoursSinceLastUse <= 1)
                    {
                        // 1小时内使用过：极高优先级
                        recencyBonus = 10000;
                    }
                    else if (hoursSinceLastUse <= 24)
                    {
                        // 24小时内使用过：高优先级
                        recencyBonus = 5000;
                    }
                    else if (hoursSinceLastUse <= 168) // 7天
                    {
                        // 7天内使用过：中等优先级
                        recencyBonus = 1000;
                    }
                    else
                    {
                        // 超过7天：使用时间衰减
                        double daysSinceLastUse = hoursSinceLastUse / 24;
                        recencyBonus = Math.Max(0, 500 - daysSinceLastUse * 10);
                    }
                }

                // 使用次数作为次要排序因素
                double frequencyScore = UsageCount * 10;

                return recencyBonus + frequencyScore;
            }
        }
    }
}
