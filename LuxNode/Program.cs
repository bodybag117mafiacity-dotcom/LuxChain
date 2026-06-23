using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<LuxState>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
var luxState = app.Services.GetRequiredService<LuxState>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

// ---------------------------------------------------------
// LUXCHAIN MAINNET – CONFIG & GLOBAL STATE
// ---------------------------------------------------------

var LuxConfig = new LuxChainConfig
{
    NetworkName = "LuxChain Mainnet",
    NetworkId = 777,
    ChainId = 777,
    Difficulty = 4,          // rapide
    BlockReward = 1900m,     // 1900 LUX par bloc (whitepaper)
    AdminApiKey = "A3F9C1E7B2D44F8A9C3E1D7F5A2B8C9E4D1F7A6B3C2E9D8F1A4B7C6D3E2F9A1"
};

List<Block> Blockchain = BlockchainStorage.Load();
Dictionary<string, AccountState> Accounts = AccountStorage.Load();
List<Transaction> Mempool = new();
LuxStats Stats = LuxStatsStorage.Load();

// ---------------------------------------------------------
// GENESIS BLOCK (UNE SEULE FOIS SI FICHIERS VIDES)
// ---------------------------------------------------------

if (Blockchain.Count == 0)
{
    var genesis = new Block(
        index: 0,
        timestamp: DateTime.UtcNow.ToString("O"),
        transactions: new List<Transaction>(),
        previousHash: "0"
    );

    genesis.Hash = genesis.CalculateHash();
    Blockchain.Add(genesis);

    // Compte GENESIS (optionnel, pour tests ou allocation initiale)
    Accounts["GENESIS"] = new AccountState
    {
        Address = "GENESIS",
        Balance = 1_000_000m
    };

    // Stats initiales
    Stats.TotalSupply = Accounts.Values.Sum(a => a.Balance);
    Stats.BlockCount = Blockchain.Count;
    Stats.TxCount = 0;
    Stats.StartTime = DateTime.UtcNow;

    BlockchainStorage.Save(Blockchain);
    AccountStorage.Save(Accounts);
    LuxStatsStorage.Save(Stats);
}

// ---------------------------------------------------------
// MIDDLEWARE SIMPLE POUR LOGS
// ---------------------------------------------------------

app.Use(async (context, next) =>
{
    var path = context.Request.Path.ToString();
    var method = context.Request.Method;
    Console.WriteLine($"[{DateTime.UtcNow:O}] {method} {path}");
    await next();
});

// ---------------------------------------------------------
// HELPERS – RÉPONSES STANDARDISÉES
// ---------------------------------------------------------

IResult OkResponse(object? data) =>
    Results.Json(new RpcResponse { Success = true, Data = data, Error = null });

IResult ErrorResponse(string message, string code = "ERR_GENERIC", int statusCode = 400) =>
    Results.Json(new RpcResponse
    {
        Success = false,
        Data = null,
        Error = new RpcError { Code = code, Message = message }
    }, statusCode: statusCode);

// ---------------------------------------------------------
// MIDDLEWARE ADMIN KEY
// ---------------------------------------------------------

bool IsAdmin(HttpRequest request)
{
    if (!request.Headers.TryGetValue("X-API-KEY", out var key))
        return false;

    return key == LuxConfig.AdminApiKey;
}

// ---------------------------------------------------------
// RPC ENDPOINTS – BLOCKCHAIN
// ---------------------------------------------------------
app.MapGet("/nonce/{address}", (string address) =>
{
    if (!luxState.Nonces.TryGetValue(address, out ulong nonce))
    {
        nonce = 0;
        luxState.Nonces[address] = 0;
    }

    return OkResponse(new { address, nonce });
});

app.MapGet("/chain", () => OkResponse(Blockchain));

app.MapGet("/block/{height:int}", (int height) =>
{
    if (height < 0 || height >= Blockchain.Count)
        return ErrorResponse("Block not found", "ERR_BLOCK_NOT_FOUND", 404);

    return OkResponse(Blockchain[height]);
});

app.MapGet("/block/hash/{hash}", (string hash) =>
{
    var block = Blockchain.FirstOrDefault(b => b.Hash == hash);
    if (block == null)
        return ErrorResponse("Block not found", "ERR_BLOCK_NOT_FOUND", 404);

    return OkResponse(block);
});

app.MapGet("/height", () => OkResponse(new { height = Blockchain.Count - 1 }));

app.MapGet("/difficulty", () => OkResponse(new { difficulty = LuxConfig.Difficulty }));

// ---------------------------------------------------------
// RPC ENDPOINTS – ACCOUNTS / WALLET
// ---------------------------------------------------------

app.MapGet("/accounts", () => OkResponse(Accounts.Values));

app.MapGet("/account/{address}", (string address) =>
{
    if (!Accounts.ContainsKey(address))
        return ErrorResponse("Account not found", "ERR_ACCOUNT_NOT_FOUND", 404);

    return OkResponse(Accounts[address]);
});

app.MapGet("/balance/{address}", (string address) =>
{
    if (!Accounts.ContainsKey(address))
        return OkResponse(new { address, balance = 0m });

    return OkResponse(new { address, balance = Accounts[address].Balance });
});

// Génération d'une adresse simple (clé publique = base64, adresse = hash)
app.MapPost("/wallet/create", () =>
{
    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var pubKey = ecdsa.ExportSubjectPublicKeyInfo();
    var pubKeyBase64 = Convert.ToBase64String(pubKey);

    // Adresse = SHA256(pubKey) tronqué
    using var sha = SHA256.Create();
    var hash = sha.ComputeHash(pubKey);
    var address = Convert.ToHexString(hash).Substring(0, 40);

    if (!Accounts.ContainsKey(address))
    {
        Accounts[address] = new AccountState
        {
            Address = address,
            Balance = 0m
        };
        AccountStorage.Save(Accounts);
    }

    return OkResponse(new
    {
        address,
        publicKey = pubKeyBase64,
        note = "La clé privée doit être gérée côté client (non générée ici)."
    });
});

// ---------------------------------------------------------
// RPC ENDPOINTS – MEMPOOL & TRANSACTIONS
// ---------------------------------------------------------

app.MapGet("/mempool", () => OkResponse(Mempool));

app.MapPost("/tx/send", ([FromBody] Transaction tx) =>
{
    // Vérif signature
    if (!Crypto.VerifySignature(tx.FromPublicKey, tx.Signature, tx.DataToSign()))
        return ErrorResponse("Invalid signature", "ERR_INVALID_SIGNATURE");

    // Vérif comptes
    if (!Accounts.ContainsKey(tx.From))
        return ErrorResponse("Sender account not found", "ERR_SENDER_NOT_FOUND");

    if (!Accounts.ContainsKey(tx.To))
        Accounts[tx.To] = new AccountState { Address = tx.To, Balance = 0m };

    if (Accounts[tx.From].Balance < tx.Amount)
        return ErrorResponse("Insufficient balance", "ERR_INSUFFICIENT_FUNDS");

    // Pas d'application immédiate, juste ajout au mempool
    tx.Hash = tx.CalculateHash();
    Mempool.Add(tx);

    Stats.TxCount++;
    LuxStatsStorage.Save(Stats);

    return OkResponse(new { message = "Transaction added to mempool", txHash = tx.Hash });
});

app.MapGet("/tx/{hash}", (string hash) =>
{
    var tx = Mempool.FirstOrDefault(t => t.Hash == hash)
             ?? Blockchain.SelectMany(b => b.Transactions).FirstOrDefault(t => t.Hash == hash);

    if (tx == null)
        return ErrorResponse("Transaction not found", "ERR_TX_NOT_FOUND", 404);

    return OkResponse(tx);
});

// ---------------------------------------------------------
// RPC ENDPOINTS – MINING
// ---------------------------------------------------------

// getwork : donne le header brut + target
app.MapGet("/mine/getwork", () =>
{
    var lastBlock = Blockchain.Last();
    var candidate = new Block(
        index: lastBlock.Index + 1,
        timestamp: DateTime.UtcNow.ToString("O"),
        transactions: new List<Transaction>(Mempool),
        previousHash: lastBlock.Hash
    );

    string targetPrefix = new string('0', LuxConfig.Difficulty);

    return OkResponse(new
    {
        index = candidate.Index,
        timestamp = candidate.Timestamp,
        previousHash = candidate.PreviousHash,
        difficulty = LuxConfig.Difficulty,
        targetPrefix,
        txCount = candidate.Transactions.Count
    });
});

// submit : soumission d'un bloc miné (simplifié)
app.MapPost("/mine/submit", ([FromBody] MinedBlockSubmission submission) =>
{
    var lastBlock = Blockchain.Last();

    if (submission.Index != lastBlock.Index + 1 || submission.PreviousHash != lastBlock.Hash)
        return ErrorResponse("Invalid block linkage", "ERR_INVALID_BLOCK");

    var newBlock = new Block(
        index: submission.Index,
        timestamp: submission.Timestamp,
        transactions: new List<Transaction>(Mempool),
        previousHash: submission.PreviousHash
    );

    newBlock.Nonce = submission.Nonce;
    newBlock.Hash = newBlock.CalculateHash();

    string target = new string('0', LuxConfig.Difficulty);
    if (!newBlock.Hash.StartsWith(target))
        return ErrorResponse("Invalid proof of work", "ERR_INVALID_POW");

    // Appliquer les transactions
    foreach (var tx in Mempool)
    {
        if (!Accounts.ContainsKey(tx.From))
            return ErrorResponse("Sender account not found during apply", "ERR_SENDER_NOT_FOUND");

        if (!Accounts.ContainsKey(tx.To))
            Accounts[tx.To] = new AccountState { Address = tx.To, Balance = 0m };

        if (Accounts[tx.From].Balance < tx.Amount)
            return ErrorResponse("Insufficient balance during apply", "ERR_INSUFFICIENT_FUNDS");

        Accounts[tx.From].Balance -= tx.Amount;
        Accounts[tx.To].Balance += tx.Amount;
    }

    // Récompense du mineur
    if (!string.IsNullOrWhiteSpace(submission.MinerAddress))
    {
        if (!Accounts.ContainsKey(submission.MinerAddress))
            Accounts[submission.MinerAddress] = new AccountState { Address = submission.MinerAddress, Balance = 0m };

        Accounts[submission.MinerAddress].Balance += LuxConfig.BlockReward;
        Stats.TotalSupply += LuxConfig.BlockReward;
    }

    Blockchain.Add(newBlock);
    Mempool.Clear();

    Stats.BlockCount = Blockchain.Count;
    LuxStatsStorage.Save(Stats);
    BlockchainStorage.Save(Blockchain);
    AccountStorage.Save(Accounts);

    return OkResponse(new { message = "Block accepted", blockHash = newBlock.Hash });
});

// Mine manuel (admin, pour tests)
app.MapPost("/mine/force", (HttpRequest request) =>
{
    if (!IsAdmin(request))
        return ErrorResponse("Unauthorized", "ERR_UNAUTHORIZED", 401);

    if (Mempool.Count == 0)
        return ErrorResponse("Mempool empty", "ERR_MEMPOOL_EMPTY");

    var lastBlock = Blockchain.Last();

    var newBlock = new Block(
        index: lastBlock.Index + 1,
        timestamp: DateTime.UtcNow.ToString("O"),
        transactions: new List<Transaction>(Mempool),
        previousHash: lastBlock.Hash
    );

    newBlock.Mine(LuxConfig.Difficulty);

    // Appliquer les transactions
    foreach (var tx in Mempool)
    {
        if (!Accounts.ContainsKey(tx.From))
            return ErrorResponse("Sender account not found during apply", "ERR_SENDER_NOT_FOUND");

        if (!Accounts.ContainsKey(tx.To))
            Accounts[tx.To] = new AccountState { Address = tx.To, Balance = 0m };

        if (Accounts[tx.From].Balance < tx.Amount)
            return ErrorResponse("Insufficient balance during apply", "ERR_INSUFFICIENT_FUNDS");

        Accounts[tx.From].Balance -= tx.Amount;
        Accounts[tx.To].Balance += tx.Amount;
    }

    // Récompense admin (optionnel)
    const string adminMiner = "ADMIN_MINER";
    if (!Accounts.ContainsKey(adminMiner))
        Accounts[adminMiner] = new AccountState { Address = adminMiner, Balance = 0m };

    Accounts[adminMiner].Balance += LuxConfig.BlockReward;
    Stats.TotalSupply += LuxConfig.BlockReward;

    Blockchain.Add(newBlock);
    Mempool.Clear();

    Stats.BlockCount = Blockchain.Count;
    LuxStatsStorage.Save(Stats);
    BlockchainStorage.Save(Blockchain);
    AccountStorage.Save(Accounts);

    return OkResponse(new { message = "Block mined (force)", blockHash = newBlock.Hash });
});

// ---------------------------------------------------------
// RPC ENDPOINTS – NODE / STATS / HEALTH
// ---------------------------------------------------------

app.MapGet("/node/info", () =>
{
    return OkResponse(new
    {
        network = LuxConfig.NetworkName,
        networkId = LuxConfig.NetworkId,
        chainId = LuxConfig.ChainId,
        difficulty = LuxConfig.Difficulty,
        blockReward = LuxConfig.BlockReward,
        blockHeight = Blockchain.Count - 1,
        totalSupply = Stats.TotalSupply,
        txCount = Stats.TxCount,
        uptimeSeconds = (DateTime.UtcNow - Stats.StartTime).TotalSeconds
    });
});

app.MapGet("/node/health", () =>
{
    bool ok = Blockchain.Count > 0;
    return OkResponse(new
    {
        status = ok ? "OK" : "ERROR",
        blockHeight = Blockchain.Count - 1,
        mempoolSize = Mempool.Count
    });
});

app.MapGet("/stats/supply", () =>
{
    return OkResponse(new
    {
        totalSupply = Stats.TotalSupply,
        blockCount = Stats.BlockCount,
        txCount = Stats.TxCount
    });
});

// TPS approximatif (txCount / uptime)
app.MapGet("/stats/tps", () =>
{
    var uptimeSeconds = (DateTime.UtcNow - Stats.StartTime).TotalSeconds;
    double tps = uptimeSeconds > 0 ? Stats.TxCount / uptimeSeconds : 0;
    return OkResponse(new
    {
        tps,
        txCount = Stats.TxCount,
        uptimeSeconds
    });
});

// ---------------------------------------------------------
// RPC ENDPOINTS – ADMIN (PROTÉGÉ)
// ---------------------------------------------------------

app.MapPost("/admin/resetChain", (HttpRequest request) =>
{
    if (!IsAdmin(request))
        return ErrorResponse("Unauthorized", "ERR_UNAUTHORIZED", 401);

    Blockchain.Clear();
    Accounts.Clear();
    Mempool.Clear();

    var genesis = new Block(
        index: 0,
        timestamp: DateTime.UtcNow.ToString("O"),
        transactions: new List<Transaction>(),
        previousHash: "0"
    );
    genesis.Hash = genesis.CalculateHash();
    Blockchain.Add(genesis);

    Accounts["GENESIS"] = new AccountState
    {
        Address = "GENESIS",
        Balance = 1_000_000m
    };

    Stats = new LuxStats
    {
        TotalSupply = Accounts.Values.Sum(a => a.Balance),
        BlockCount = Blockchain.Count,
        TxCount = 0,
        StartTime = DateTime.UtcNow
    };

    BlockchainStorage.Save(Blockchain);
    AccountStorage.Save(Accounts);
    LuxStatsStorage.Save(Stats);

    return OkResponse(new { message = "Chain reset done" });
});

app.MapPost("/admin/setDifficulty/{difficulty:int}", (HttpRequest request, int difficulty) =>
{
    if (!IsAdmin(request))
        return ErrorResponse("Unauthorized", "ERR_UNAUTHORIZED", 401);

    if (difficulty < 1 || difficulty > 8)
        return ErrorResponse("Invalid difficulty range", "ERR_INVALID_DIFFICULTY");

    LuxConfig.Difficulty = difficulty;
    return OkResponse(new { message = "Difficulty updated", difficulty });
});

app.MapPost("/admin/mint", (HttpRequest request, [FromBody] MintRequest mint) =>
{
    if (!IsAdmin(request))
        return ErrorResponse("Unauthorized", "ERR_UNAUTHORIZED", 401);

    if (mint.Amount <= 0)
        return ErrorResponse("Invalid amount", "ERR_INVALID_AMOUNT");

    if (!Accounts.ContainsKey(mint.Address))
        Accounts[mint.Address] = new AccountState { Address = mint.Address, Balance = 0m };

    Accounts[mint.Address].Balance += mint.Amount;
    Stats.TotalSupply += mint.Amount;

    AccountStorage.Save(Accounts);
    LuxStatsStorage.Save(Stats);

    return OkResponse(new { message = "Mint done", address = mint.Address, amount = mint.Amount });
});

// ---------------------------------------------------------
// RUN
// ---------------------------------------------------------

app.Run();

// ---------------------------------------------------------
// DOMAIN CLASSES
// ---------------------------------------------------------

public class Block
{
    public int Index { get; set; }
    public string Timestamp { get; set; }
    public List<Transaction> Transactions { get; set; }
    public string PreviousHash { get; set; }
    public string Hash { get; set; }
    public long Nonce { get; set; }

    public Block(int index, string timestamp, List<Transaction> transactions, string previousHash)
    {
        Index = index;
        Timestamp = timestamp;
        Transactions = transactions;
        PreviousHash = previousHash;
        Hash = "";
        Nonce = 0;
    }

    public string CalculateHash()
    {
        string blockData = $"{Index}{Timestamp}{JsonSerializer.Serialize(Transactions)}{PreviousHash}{Nonce}";
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(blockData)));
    }

    public void Mine(int difficulty)
    {
        string target = new string('0', difficulty);

        while (!Hash.StartsWith(target))
        {
            Nonce++;
            Hash = CalculateHash();
        }
    }
}

public class Transaction
{
    public string From { get; set; }           // adresse
    public string FromPublicKey { get; set; }  // clé publique
    public string To { get; set; }             // adresse
    public decimal Amount { get; set; }
    public string Signature { get; set; }
    public string Hash { get; set; }

    public string DataToSign() => $"{From}{To}{Amount}";

    public string CalculateHash()
    {
        using var sha = SHA256.Create();
        var raw = $"{From}{To}{Amount}{Signature}";
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw)));
    }
}

public class AccountState
{
    public string Address { get; set; }
    public decimal Balance { get; set; }
}

// ---------------------------------------------------------
// RPC MODELS
// ---------------------------------------------------------

public class RpcResponse
{
    public bool Success { get; set; }
    public object? Data { get; set; }
    public RpcError? Error { get; set; }
}

public class RpcError
{
    public string Code { get; set; }
    public string Message { get; set; }
}

public class MinedBlockSubmission
{
    public int Index { get; set; }
    public string Timestamp { get; set; }
    public string PreviousHash { get; set; }
    public long Nonce { get; set; }
    public string MinerAddress { get; set; }
}

public class MintRequest
{
    public string Address { get; set; }
    public decimal Amount { get; set; }
}

public class LuxChainConfig
{
    public string NetworkName { get; set; }
    public int NetworkId { get; set; }
    public int ChainId { get; set; }
    public int Difficulty { get; set; }
    public decimal BlockReward { get; set; }
    public string AdminApiKey { get; set; }
}

public class LuxStats
{
    public decimal TotalSupply { get; set; }
    public int BlockCount { get; set; }
    public long TxCount { get; set; }
    public DateTime StartTime { get; set; }
}

// ---------------------------------------------------------
// CRYPTO
// ---------------------------------------------------------

public static class Crypto
{
    public static bool VerifySignature(string publicKey, string signature, string data)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);

            return ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(data),
                Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256
            );
        }
        catch
        {
            return false;
        }
    }
}

// ---------------------------------------------------------
// STORAGE
// ---------------------------------------------------------

public static class BlockchainStorage
{
    private static string Path = "blockchain.json";

    public static List<Block> Load()
    {
        if (!File.Exists(Path))
            return new List<Block>();

        string json = File.ReadAllText(Path);
        return JsonSerializer.Deserialize<List<Block>>(json) ?? new List<Block>();
    }

    public static void Save(List<Block> chain)
    {
        string json = JsonSerializer.Serialize(chain, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path, json);
    }
}

public static class AccountStorage
{
    private static string Path = "accounts.json";

    public static Dictionary<string, AccountState> Load()
    {
        if (!File.Exists(Path))
            return new Dictionary<string, AccountState>();

        string json = File.ReadAllText(Path);
        return JsonSerializer.Deserialize<Dictionary<string, AccountState>>(json)
               ?? new Dictionary<string, AccountState>();
    }

    public static void Save(Dictionary<string, AccountState> accounts)
    {
        string json = JsonSerializer.Serialize(accounts, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path, json);
    }
}

public static class LuxStatsStorage
{
    private static string Path = "stats.json";

    public static LuxStats Load()
    {
        if (!File.Exists(Path))
            return new LuxStats
            {
                TotalSupply = 0,
                BlockCount = 0,
                TxCount = 0,
                StartTime = DateTime.UtcNow
            };

        string json = File.ReadAllText(Path);
        return JsonSerializer.Deserialize<LuxStats>(json)
               ?? new LuxStats
               {
                   TotalSupply = 0,
                   BlockCount = 0,
                   TxCount = 0,
                   StartTime = DateTime.UtcNow
               };
    }

    public static void Save(LuxStats stats)
    {
        string json = JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path, json);
    }
}
