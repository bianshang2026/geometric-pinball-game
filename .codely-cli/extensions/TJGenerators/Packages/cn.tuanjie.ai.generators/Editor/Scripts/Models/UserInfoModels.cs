#if UNITY_EDITOR
using System;

namespace TJGenerators
{
    // ========== 用户信息 API 响应 ==========

    [Serializable]
    public class UserInfoResponse
    {
        public string avatar;
        public UserCredits credits;
        public string email;
        public string genesisUserId;
        public string id;
        public bool isAdmin;
        public bool isCP;
        public string loginType;
        public UserOrg org;
        public string phone;
        public string role;
        public string type;
        public string username;
    }

    [Serializable]
    public class UserCredits
    {
        public int currentCredits;
        public string email;
        public string lastCreditDate;
        public int todayEarned;
        public int todaySpent;
        public int totalEarned;
        public int totalSpent;
        public string userId;
        public string username;
    }

    [Serializable]
    public class UserOrg
    {
        public string orgDisplayName;
        public string orgId;
        public string orgName;
    }
}
#endif
