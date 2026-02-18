# Continuing Architecture Design

## 5. Programmable Coupon Payments

### Architecture

**Off-Chain (C# API):**

```csharp
// Entity Models
public class CouponPaymentSchedule
{
    public Guid Id { get; set; }
    public Guid BondInstanceId { get; set; }
    public int PaymentsPerYear { get; set; } // 2 for semi-annual, 4 for quarterly
    public DateTime FirstPaymentDate { get; set; }
    public DateTime LastPaymentDate { get; set; }
    
    public BondInstance BondInstance { get; set; }
    public List<CouponPayment> Payments { get; set; }
}

public class CouponPayment
{
    public Guid Id { get; set; }
    public Guid CouponPaymentScheduleId { get; set; }
    public int PaymentNumber { get; set; }
    public DateTime RecordDate { get; set; }
    public DateTime PaymentDate { get; set; }
    public decimal CouponRate { get; set; }
    public decimal TotalPaymentAmount { get; set; }
    
    public CouponPaymentStatus Status { get; set; }
    public DateTime? ExecutedDate { get; set; }
    public string TransactionHash { get; set; }
    
    public List<CouponPaymentDetail> PaymentDetails { get; set; }
}

public class CouponPaymentDetail
{
    public Guid Id { get; set; }
    public Guid CouponPaymentId { get; set; }
    public string HolderAddress { get; set; }
    public decimal BondBalance { get; set; }
    public decimal PaymentAmount { get; set; }
    public bool IsPaid { get; set; }
    public string PaymentTxHash { get; set; }
}

public enum CouponPaymentStatus
{
    Scheduled,
    RecordDateReached,
    InProgress,
    Completed,
    Failed
}

// Service Layer
public class CouponPaymentService
{
    private readonly ApplicationDbContext _context;
    private readonly IChainlinkCREService _chainlinkService;
    private readonly ILogger<CouponPaymentService> _logger;
    
    public async Task<CouponPaymentSchedule> CreatePaymentSchedule(
        Guid bondInstanceId,
        int paymentsPerYear)
    {
        var bond = await _context.BondInstances.FindAsync(bondInstanceId);
        
        var schedule = new CouponPaymentSchedule
        {
            Id = Guid.NewGuid(),
            BondInstanceId = bondInstanceId,
            PaymentsPerYear = paymentsPerYear,
            FirstPaymentDate = CalculateFirstPaymentDate(bond, paymentsPerYear),
            LastPaymentDate = bond.MaturityDate,
            Payments = new List<CouponPayment>()
        };
        
        // Generate all payment dates
        var currentDate = schedule.FirstPaymentDate;
        int paymentNumber = 1;
        
        while (currentDate <= bond.MaturityDate)
        {
            var recordDate = currentDate.AddDays(-15); // Record date 15 days before payment
            
            var payment = new CouponPayment
            {
                Id = Guid.NewGuid(),
                CouponPaymentScheduleId = schedule.Id,
                PaymentNumber = paymentNumber,
                RecordDate = recordDate,
                PaymentDate = currentDate,
                CouponRate = bond.CouponRate,
                Status = CouponPaymentStatus.Scheduled
            };
            
            schedule.Payments.Add(payment);
            
            currentDate = currentDate.AddMonths(12 / paymentsPerYear);
            paymentNumber++;
        }
        
        _context.CouponPaymentSchedules.Add(schedule);
        await _context.SaveChangesAsync();
        
        return schedule;
    }
    
    private DateTime CalculateFirstPaymentDate(BondInstance bond, int paymentsPerYear)
    {
        return bond.IssueDate.AddMonths(12 / paymentsPerYear);
    }
    
    public async Task ProcessRecordDate(Guid couponPaymentId)
    {
        var payment = await _context.CouponPayments
            .Include(p => p.CouponPaymentSchedule)
            .ThenInclude(s => s.BondInstance)
            .FirstOrDefaultAsync(p => p.Id == couponPaymentId);
        
        if (payment.Status != CouponPaymentStatus.Scheduled)
            throw new InvalidOperationException("Payment not in scheduled status");
        
        var bond = payment.CouponPaymentSchedule.BondInstance;
        
        // Request holder snapshot from blockchain via Chainlink
        var holders = await GetHoldersSnapshot(bond.ContractAddress, payment.RecordDate);
        
        // Calculate payment amounts
        decimal totalBondSupply = holders.Sum(h => h.Balance);
        decimal couponPaymentPerBond = (bond.FaceValue * payment.CouponRate / 100m) 
            / payment.CouponPaymentSchedule.PaymentsPerYear;
        
        payment.TotalPaymentAmount = totalBondSupply * couponPaymentPerBond;
        payment.PaymentDetails = new List<CouponPaymentDetail>();
        
        foreach (var holder in holders)
        {
            var detail = new CouponPaymentDetail
            {
                Id = Guid.NewGuid(),
                CouponPaymentId = payment.Id,
                HolderAddress = holder.Address,
                BondBalance = holder.Balance,
                PaymentAmount = holder.Balance * couponPaymentPerBond,
                IsPaid = false
            };
            
            payment.PaymentDetails.Add(detail);
        }
        
        payment.Status = CouponPaymentStatus.RecordDateReached;
        await _context.SaveChangesAsync();
        
        _logger.LogInformation(
            $"Record date processed for payment {payment.Id}. " +
            $"Total holders: {holders.Count}, Total amount: {payment.TotalPaymentAmount}"
        );
    }
    
    private async Task<List<HolderSnapshot>> GetHoldersSnapshot(
        string contractAddress,
        DateTime recordDate)
    {
        var workflowRequest = new ChainlinkWorkflowRequest
        {
            WorkflowId = "get-holders-snapshot",
            Parameters = new Dictionary<string, object>
            {
                ["contractAddress"] = contractAddress,
                ["recordDate"] = recordDate.ToUnixTimeSeconds()
            }
        };
        
        var response = await _chainlinkService.ExecuteWorkflow(workflowRequest);
        return response.Data.ToObject<List<HolderSnapshot>>();
    }
    
    public async Task ExecuteCouponPayment(Guid couponPaymentId)
    {
        var payment = await _context.CouponPayments
            .Include(p => p.PaymentDetails)
            .Include(p => p.CouponPaymentSchedule)
            .ThenInclude(s => s.BondInstance)
            .FirstOrDefaultAsync(p => p.Id == couponPaymentId);
        
        if (payment.Status != CouponPaymentStatus.RecordDateReached)
            throw new InvalidOperationException("Record date not processed");
        
        var bond = payment.CouponPaymentSchedule.BondInstance;
        
        payment.Status = CouponPaymentStatus.InProgress;
        await _context.SaveChangesAsync();
        
        try
        {
            // Execute payment via Chainlink CRE
            var txHash = await ExecutePaymentOnChain(bond, payment);
            
            payment.Status = CouponPaymentStatus.Completed;
            payment.ExecutedDate = DateTime.UtcNow;
            payment.TransactionHash = txHash;
            
            // Mark all details as paid
            foreach (var detail in payment.PaymentDetails)
            {
                detail.IsPaid = true;
                detail.PaymentTxHash = txHash;
            }
            
            await _context.SaveChangesAsync();
            
            _logger.LogInformation(
                $"Coupon payment {payment.Id} completed. TxHash: {txHash}"
            );
        }
        catch (Exception ex)
        {
            payment.Status = CouponPaymentStatus.Failed;
            await _context.SaveChangesAsync();
            
            _logger.LogError(ex, $"Failed to execute coupon payment {payment.Id}");
            throw;
        }
    }
    
    private async Task<string> ExecutePaymentOnChain(
        BondInstance bond,
        CouponPayment payment)
    {
        // Prepare payment batch
        var recipients = payment.PaymentDetails
            .Select(d => d.HolderAddress)
            .ToList();
        
        var amounts = payment.PaymentDetails
            .Select(d => d.PaymentAmount)
            .ToList();
        
        var workflowRequest = new ChainlinkWorkflowRequest
        {
            WorkflowId = "execute-coupon-payment",
            Parameters = new Dictionary<string, object>
            {
                ["bondContract"] = bond.ContractAddress,
                ["paymentNumber"] = payment.PaymentNumber,
                ["recipients"] = recipients,
                ["amounts"] = amounts,
                ["totalAmount"] = payment.TotalPaymentAmount
            }
        };
        
        var response = await _chainlinkService.ExecuteWorkflow(workflowRequest);
        return response.TransactionHash;
    }
    
    public async Task AutoProcessDuePayments()
    {
        var now = DateTime.UtcNow;
        
        // Process record dates
        var dueRecordDates = await _context.CouponPayments
            .Where(p => p.Status == CouponPaymentStatus.Scheduled 
                && p.RecordDate <= now)
            .ToListAsync();
        
        foreach (var payment in dueRecordDates)
        {
            try
            {
                await ProcessRecordDate(payment.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, 
                    $"Failed to process record date for payment {payment.Id}");
            }
        }
        
        // Execute payments
        var duePayments = await _context.CouponPayments
            .Where(p => p.Status == CouponPaymentStatus.RecordDateReached 
                && p.PaymentDate <= now)
            .ToListAsync();
        
        foreach (var payment in duePayments)
        {
            try
            {
                await ExecuteCouponPayment(payment.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, 
                    $"Failed to execute payment {payment.Id}");
            }
        }
    }
}

public class HolderSnapshot
{
    public string Address { get; set; }
    public decimal Balance { get; set; }
}

// Background Service
public class CouponPaymentBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CouponPaymentBackgroundService> _logger;
    
    public CouponPaymentBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<CouponPaymentBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Coupon Payment Background Service started");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var couponService = scope.ServiceProvider
                    .GetRequiredService<CouponPaymentService>();
                
                await couponService.AutoProcessDuePayments();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in coupon payment processing");
            }
            
            // Check every hour
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
```

**Chainlink CRE Workflow:**

```javascript
// Chainlink Function for getting holder snapshot
const getHoldersSnapshot = async (args) => {
  const { contractAddress, recordDate } = args;
  
  // Create contract interface
  const bondInterface = new ethers.utils.Interface([
    "function balanceOf(address) view returns (uint256)",
    "function totalSupply() view returns (uint256)"
  ]);
  
  // Query blockchain for Transfer events to build holder list
  const provider = new ethers.providers.JsonRpcProvider(
    process.env.RPC_URL
  );
  
  const contract = new ethers.Contract(
    contractAddress,
    bondInterface,
    provider
  );
  
  // Get all transfer events up to record date block
  const recordBlock = await getBlockAtTimestamp(provider, recordDate);
  
  const filter = contract.filters.Transfer();
  const events = await contract.queryFilter(filter, 0, recordBlock);
  
  // Build holder map
  const holders = new Map();
  
  for (const event of events) {
    const { from, to, value } = event.args;
    
    // Subtract from sender
    if (from !== ethers.constants.AddressZero) {
      const balance = holders.get(from) || ethers.BigNumber.from(0);
      holders.set(from, balance.sub(value));
    }
    
    // Add to receiver
    if (to !== ethers.constants.AddressZero) {
      const balance = holders.get(to) || ethers.BigNumber.from(0);
      holders.set(to, balance.add(value));
    }
  }
  
  // Convert to array, filter out zero balances
  const snapshot = [];
  for (const [address, balance] of holders) {
    if (balance.gt(0)) {
      snapshot.push({
        address,
        balance: ethers.utils.formatUnits(balance, 18)
      });
    }
  }
  
  return Functions.encodeBytes(JSON.stringify(snapshot));
};

// Helper to find block at specific timestamp
const getBlockAtTimestamp = async (provider, timestamp) => {
  let high = await provider.getBlockNumber();
  let low = 0;
  
  while (low < high) {
    const mid = Math.floor((low + high) / 2);
    const block = await provider.getBlock(mid);
    
    if (block.timestamp < timestamp) {
      low = mid + 1;
    } else {
      high = mid;
    }
  }
  
  return low;
};

// Chainlink Function for executing payment
const executeCouponPayment = async (args) => {
  const { bondContract, paymentNumber, recipients, amounts, totalAmount } = args;
  
  // Encode batch payment call
  const distributorInterface = new ethers.utils.Interface([
    "function distributeCouponPayment(uint256,address[],uint256[])"
  ]);
  
  const encodedData = distributorInterface.encodeFunctionData(
    "distributeCouponPayment",
    [paymentNumber, recipients, amounts]
  );
  
  return Functions.encodeBytes(encodedData);
};
```

**On-Chain Smart Contracts:**

```solidity
// CouponDistributor.sol
pragma solidity ^0.8.20;

import "@openzeppelin/contracts/token/ERC20/IERC20.sol";
import "@openzeppelin/contracts/access/AccessControl.sol";
import "@openzeppelin/contracts/security/ReentrancyGuard.sol";

contract CouponDistributor is AccessControl, ReentrancyGuard {
    bytes32 public constant ORACLE_ROLE = keccak256("ORACLE_ROLE");
    bytes32 public constant TREASURER_ROLE = keccak256("TREASURER_ROLE");
    
    // Payment token (e.g., USDC for coupon payments)
    IERC20 public paymentToken;
    
    struct CouponPaymentRecord {
        uint256 paymentNumber;
        uint256 totalAmount;
        uint256 recipientCount;
        uint256 executionTimestamp;
        bool isExecuted;
    }
    
    // Mapping: bond contract => payment number => payment record
    mapping(address => mapping(uint256 => CouponPaymentRecord)) 
        public paymentRecords;
    
    // Mapping: bond contract => holder => payment number => amount received
    mapping(address => mapping(address => mapping(uint256 => uint256)))
        public holderPayments;
    
    event CouponPaymentDistributed(
        address indexed bondContract,
        uint256 paymentNumber,
        uint256 totalAmount,
        uint256 recipientCount
    );
    
    event CouponPaid(
        address indexed bondContract,
        address indexed recipient,
        uint256 paymentNumber,
        uint256 amount
    );
    
    constructor(address _paymentToken) {
        paymentToken = IERC20(_paymentToken);
        _grantRole(DEFAULT_ADMIN_ROLE, msg.sender);
    }
    
    function distributeCouponPayment(
        address _bondContract,
        uint256 _paymentNumber,
        address[] calldata _recipients,
        uint256[] calldata _amounts
    ) external onlyRole(ORACLE_ROLE) nonReentrant {
        require(_recipients.length == _amounts.length, "Length mismatch");
        require(
            !paymentRecords[_bondContract][_paymentNumber].isExecuted,
            "Payment already executed"
        );
        
        uint256 totalAmount = 0;
        
        // Execute batch payment
        for (uint256 i = 0; i < _recipients.length; i++) {
            require(_recipients[i] != address(0), "Invalid recipient");
            require(_amounts[i] > 0, "Invalid amount");
            
            // Transfer payment token
            require(
                paymentToken.transfer(_recipients[i], _amounts[i]),
                "Transfer failed"
            );
            
            // Record payment
            holderPayments[_bondContract][_recipients[i]][_paymentNumber] = _amounts[i];
            totalAmount += _amounts[i];
            
            emit CouponPaid(
                _bondContract,
                _recipients[i],
                _paymentNumber,
                _amounts[i]
            );
        }
        
        // Record payment execution
        paymentRecords[_bondContract][_paymentNumber] = CouponPaymentRecord({
            paymentNumber: _paymentNumber,
            totalAmount: totalAmount,
            recipientCount: _recipients.length,
            executionTimestamp: block.timestamp,
            isExecuted: true
        });
        
        emit CouponPaymentDistributed(
            _bondContract,
            _paymentNumber,
            totalAmount,
            _recipients.length
        );
    }
    
    function fundDistributor(uint256 _amount) 
        external 
        onlyRole(TREASURER_ROLE) 
    {
        require(
            paymentToken.transferFrom(msg.sender, address(this), _amount),
            "Transfer failed"
        );
    }
    
    function getPaymentRecord(address _bondContract, uint256 _paymentNumber)
        external
        view
        returns (CouponPaymentRecord memory)
    {
        return paymentRecords[_bondContract][_paymentNumber];
    }
    
    function getHolderPayment(
        address _bondContract,
        address _holder,
        uint256 _paymentNumber
    ) external view returns (uint256) {
        return holderPayments[_bondContract][_holder][_paymentNumber];
    }
    
    function withdrawExcess(address _to, uint256 _amount)
        external
        onlyRole(DEFAULT_ADMIN_ROLE)
    {
        require(paymentToken.transfer(_to, _amount), "Transfer failed");
    }
}
```

---

## 6. Conversion & Special Event Management

### A. Contingent Trigger Monitor

**Off-Chain (C# API):**

```csharp
// Entity Models
public class ContingentTrigger
{
    public Guid Id { get; set; }
    public Guid BondInstanceId { get; set; }
    public TriggerType Type { get; set; }
    public bool IsActive { get; set; }
    
    // Market Price Trigger parameters
    public decimal? TriggerPriceThreshold { get; set; } // e.g., 130% of conversion price
    public int? ConsecutiveTradingDays { get; set; } // e.g., 20 out of 30 days
    public int? ObservationPeriodDays { get; set; } // e.g., 30 days
    
    // Fundamental Change parameters
    public string? ChangeDescription { get; set; }
    
    public DateTime CreatedDate { get; set; }
    public DateTime? TriggeredDate { get; set; }
    
    public BondInstance BondInstance { get; set; }
    public List<TriggerObservation> Observations { get; set; }
}

public enum TriggerType
{
    MarketPriceTrigger,
    FundamentalChange,
    MakeWholeProvision,
    CallProvision
}

public class TriggerObservation
{
    public Guid Id { get; set; }
    public Guid ContingentTriggerId { get; set; }
    public DateTime ObservationDate { get; set; }
    public decimal StockPrice { get; set; }
    public decimal ConversionPrice { get; set; }
    public decimal PriceRatio { get; set; }
    public bool MeetsThreshold { get; set; }
}

public class ConversionEvent
{
    public Guid Id { get; set; }
    public Guid BondInstanceId { get; set; }
    public Guid? ContingentTriggerId { get; set; }
    public ConversionType Type { get; set; }
    public SettlementMethod SettlementMethod { get; set; }
    
    public string HolderAddress { get; set; }
    public decimal BondAmount { get; set; }
    public decimal ConversionRatio { get; set; }
    public decimal SharesReceivable { get; set; }
    public decimal? CashAmount { get; set; }
    
    public ConversionStatus Status { get; set; }
    public DateTime RequestedDate { get; set; }
    public DateTime? ExecutedDate { get; set; }
    public string? TransactionHash { get; set; }
    
    // Induced conversion
    public bool IsInducedConversion { get; set; }
    public decimal? InducedConsideration { get; set; }
}

public enum ConversionType
{
    Voluntary,
    ContingentTrigger,
    InducedConversion,
    MaturityConversion
}

public enum SettlementMethod
{
    PhysicalSettlement, // Shares only
    CashSettlement, // Cash only
    NetShareSettlement // Cash for principal, shares for excess
}

public enum ConversionStatus
{
    Requested,
    Validated,
    InProgress,
    Completed,
    Failed
}

// Service Layer
public class ConversionMonitorService
{
    private readonly ApplicationDbContext _context;
    private readonly IChainlinkCREService _chainlinkService;
    private readonly ILogger<ConversionMonitorService> _logger;
    
    public async Task<ContingentTrigger> CreateMarketPriceTrigger(
        Guid bondInstanceId,
        decimal thresholdPercentage,
        int consecutiveDays,
        int observationPeriod)
    {
        var bond = await _context.BondInstances
            .Include(b => b.ConvertibleParameters)
            .FirstOrDefaultAsync(b => b.Id == bondInstanceId);
        
        var conversionPrice = bond.ConvertibleParameters.ConversionPrice;
        var triggerPrice = conversionPrice * (thresholdPercentage / 100m);
        
        var trigger = new ContingentTrigger
        {
            Id = Guid.NewGuid(),
            BondInstanceId = bondInstanceId,
            Type = TriggerType.MarketPriceTrigger,
            IsActive = true,
            TriggerPriceThreshold = triggerPrice,
            ConsecutiveTradingDays = consecutiveDays,
            ObservationPeriodDays = observationPeriod,
            CreatedDate = DateTime.UtcNow,
            Observations = new List<TriggerObservation>()
        };
        
        _context.ContingentTriggers.Add(trigger);
        await _context.SaveChangesAsync();
        
        // Register trigger with oracle monitoring
        await RegisterTriggerWithOracle(trigger, bond);
        
        return trigger;
    }
    
    private async Task RegisterTriggerWithOracle(
        ContingentTrigger trigger,
        BondInstance bond)
    {
        var workflowRequest = new ChainlinkWorkflowRequest
        {
            WorkflowId = "register-price-trigger",
            Parameters = new Dictionary<string, object>
            {
                ["triggerId"] = trigger.Id.ToString(),
                ["bondContract"] = bond.ContractAddress,
                ["equitySymbol"] = bond.UnderlyingEquitySymbol,
                ["triggerPrice"] = trigger.TriggerPriceThreshold,
                ["consecutiveDays"] = trigger.ConsecutiveTradingDays,
                ["observationPeriod"] = trigger.ObservationPeriodDays
            }
        };
        
        await _chainlinkService.ExecuteWorkflow(workflowRequest);
    }
    
    public async Task RecordPriceObservation(
        Guid triggerId,
        decimal stockPrice)
    {
        var trigger = await _context.ContingentTriggers
            .Include(t => t.BondInstance)
            .ThenInclude(b => b.ConvertibleParameters)
            .Include(t => t.Observations)
            .FirstOrDefaultAsync(t => t.Id == triggerId);
        
        if (!trigger.IsActive || trigger.TriggeredDate.HasValue)
            return;
        
        var conversionPrice = trigger.BondInstance.ConvertibleParameters.ConversionPrice;
        var priceRatio = stockPrice / conversionPrice;
        var meetsThreshold = stockPrice >= trigger.TriggerPriceThreshold;
        
        var observation = new TriggerObservation
        {
            Id = Guid.NewGuid(),
            ContingentTriggerId = triggerId,
            ObservationDate = DateTime.UtcNow,
            StockPrice = stockPrice,
            ConversionPrice = conversionPrice,
            PriceRatio = priceRatio,
            MeetsThreshold = meetsThreshold
        };
        
        trigger.Observations.Add(observation);
        await _context.SaveChangesAsync();
        
        // Check if trigger condition is met
        await EvaluateTriggerCondition(trigger);
    }
    
    private async Task EvaluateTriggerCondition(ContingentTrigger trigger)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-trigger.ObservationPeriodDays.Value);
        
        var recentObservations = trigger.Observations
            .Where(o => o.ObservationDate >= cutoffDate)
            .OrderByDescending(o => o.ObservationDate)
            .ToList();
        
        if (recentObservations.Count < trigger.ObservationPeriodDays.Value)
            return; // Not enough data yet
        
        var consecutiveCount = 0;
        var maxConsecutive = 0;
        
        foreach (var obs in recentObservations)
        {
            if (obs.MeetsThreshold)
            {
                consecutiveCount++;
                maxConsecutive = Math.Max(maxConsecutive, consecutiveCount);
            }
            else
            {
                consecutiveCount = 0;
            }
        }
        
        if (maxConsecutive >= trigger.ConsecutiveTradingDays.Value)
        {
            // Trigger condition met!
            trigger.TriggeredDate = DateTime.UtcNow;
            trigger.IsActive = false;
            await _context.SaveChangesAsync();
            
            _logger.LogInformation(
                $"Contingent trigger {trigger.Id} activated for bond {trigger.BondInstanceId}"
            );
            
            // Notify blockchain
            await NotifyTriggerActivation(trigger);
        }
    }
    
    private async Task NotifyTriggerActivation(ContingentTrigger trigger)
    {
        var bond = await _context.BondInstances.FindAsync(trigger.BondInstanceId);
        
        var workflowRequest = new ChainlinkWorkflowRequest
        {
            WorkflowId = "activate-conversion-trigger",
            Parameters = new Dictionary<string, object>
            {
                ["bondContract"] = bond.ContractAddress,
                ["triggerId"] = trigger.Id.ToString(),
                ["triggerType"] = trigger.Type.ToString()
            }
        };
        
        await _chainlinkService.ExecuteWorkflow(workflowRequest);
    }
    
    public async Task CreateFundamentalChangeTrigger(
        Guid bondInstanceId,
        string changeDescription)
    {
        var trigger = new ContingentTrigger
        {
            Id = Guid.NewGuid(),
            BondInstanceId = bondInstanceId,
            Type = TriggerType.FundamentalChange,
            IsActive = true,
            ChangeDescription = changeDescription,
            CreatedDate = DateTime.UtcNow,
            TriggeredDate = DateTime.UtcNow // Immediately triggered
        };
        
        _context.ContingentTriggers.Add(trigger);
        await _context.SaveChangesAsync();
        
        var bond = await _context.BondInstances.FindAsync(bondInstanceId);
        await NotifyTriggerActivation(trigger);
        
        _logger.LogInformation(
            $"Fundamental change trigger created for bond {bondInstanceId}: {changeDescription}"
        );
    }
    
    // Background service to fetch prices from oracle
    public async Task MonitorActiveTriggers()
    {
        var activeTriggers = await _context.ContingentTriggers
            .Where(t => t.IsActive && t.Type == TriggerType.MarketPriceTrigger)
            .Include(t => t.BondInstance)
            .ToListAsync();
        
        foreach (var trigger in activeTriggers)
        {
            try
            {
                // Fetch current stock price from Chainlink oracle
                var stockPrice = await FetchStockPrice(
                    trigger.BondInstance.UnderlyingEquitySymbol
                );
                
                await RecordPriceObservation(trigger.Id, stockPrice);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, 
                    $"Error monitoring trigger {trigger.Id}");
            }
        }
    }
    
    private async Task<decimal> FetchStockPrice(string symbol)
    {
        var workflowRequest = new ChainlinkWorkflowRequest
        {
            WorkflowId = "fetch-stock-price",
            Parameters = new Dictionary<string, object>
            {
                ["symbol"] = symbol
            }
        };
        
        var response = await _chainlinkService.ExecuteWorkflow(workflowRequest);
        return Convert.ToDecimal(response.Data["price"]);
    }
}

// Background Service
public class TriggerMonitorBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TriggerMonitorBackgroundService> _logger;
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Trigger Monitor Service started");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var monitorService = scope.ServiceProvider
                    .GetRequiredService<ConversionMonitorService>();
                
                await monitorService.MonitorActiveTriggers();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in trigger monitoring");
            }
            
            // Check every 15 minutes during trading hours
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }
}
```

**Chainlink CRE Workflow for Oracle Integration:**

```javascript
// Chainlink Function to fetch stock price from multiple sources
const fetchStockPrice = async (args) => {
  const { symbol } = args;
  
  // Fetch from multiple price feeds for redundancy
  const sources = [
    `https://api.example-finance.com/stock/${symbol}/price`,
    `https://api.another-source.com/quotes/${symbol}`,
    `https://api.backup-feed.com/pricing/${symbol}`
  ];
  
  const prices = [];
  
  for (const url of sources) {
    try {
      const response = await Functions.makeHttpRequest({
        url: url,
        headers: { 'X-API-Key': secrets.apiKey }
      });
      
      if (response.data && response.data.price) {
        prices.push(parseFloat(response.data.price));
      }
    } catch (error) {
      console.log(`Error fetching from ${url}:`, error);
    }
  }
  
  if (prices.length === 0) {
    throw new Error("Failed to fetch price from any source");
  }
  
  // Calculate median price
  prices.sort((a, b) => a - b);
  const median = prices[Math.floor(prices.length / 2)];
  
  // Encode result
  const result = {
    symbol: symbol,
    price: median.toString(),
    sources: prices.length,
    timestamp: Date.now()
  };
  
  return Functions.encodeBytes(JSON.stringify(result));
};

// Chainlink Automation-compatible function to check trigger conditions
const checkTriggerConditions = async (args) => {
  const { bondContract, triggerId, triggerPrice, consecutiveDays } = args;
  
  // This would be called by Chainlink Automation on a schedule
  // Query the accounting ledger for recent observations
  
  const provider = new ethers.providers.JsonRpcProvider(
    process.env.RPC_URL
  );
  
  const oracleContract = new ethers.Contract(
    process.env.ORACLE_CONTRACT,
    ['function getRecentObservations(bytes32,uint256) view returns (bool[])'],
    provider
  );
  
  const triggerIdBytes = ethers.utils.formatBytes32String(triggerId);
  const observations = await oracleContract.getRecentObservations(
    triggerIdBytes,
    consecutiveDays
  );
  
  // Check if threshold met for required consecutive days
  let consecutiveCount = 0;
  for (const met of observations) {
    if (met) {
      consecutiveCount++;
      if (consecutiveCount >= consecutiveDays) {
        // Trigger should be activated!
        return Functions.encodeBytes("TRIGGER_ACTIVATED");
      }
    } else {
      consecutiveCount = 0;
    }
  }
  
  return Functions.encodeBytes("TRIGGER_NOT_MET");
};
```

**On-Chain Smart Contracts:**

```solidity
// OracleIntegration.sol
pragma solidity ^0.8.20;

import "@chainlink/contracts/src/v0.8/interfaces/AggregatorV3Interface.sol";
import "@openzeppelin/contracts/access/AccessControl.sol";

contract OracleIntegration is AccessControl {
    bytes32 public constant ORACLE_ROLE = keccak256("ORACLE_ROLE");
    
    struct PriceTrigger {
        bytes32 triggerId;
        address bondContract;
        string equitySymbol;
        uint256 triggerPrice;
        uint256 consecutiveDays;
        uint256 observationPeriod;
        bool isActive;
        bool isTriggered;
        uint256 activationDate;
    }
    
    struct PriceObservation {
        uint256 timestamp;
        uint256 price;
        bool meetsThreshold;
    }
    
    // Chainlink price feed addresses
    mapping(string => address) public priceFeedAddresses;
    
    // Trigger data
    mapping(bytes32 => PriceTrigger) public triggers;
    mapping(bytes32 => PriceObservation[]) public observations;
    
    event TriggerRegistered(
        bytes32 indexed triggerId,
        address indexed bondContract,
        string equitySymbol
    );
    
    event ObservationRecorded(
        bytes32 indexed triggerId,
        uint256 price,
        bool meetsThreshold
    );
    
    event TriggerActivated(
        bytes32 indexed triggerId,
        address indexed bondContract
    );
    
    constructor() {
        _grantRole(DEFAULT_ADMIN_ROLE, msg.sender);
    }
    
    function registerPriceFeed(
        string memory _symbol,
        address _feedAddress
    ) external onlyRole(DEFAULT_ADMIN_ROLE) {
        priceFeedAddresses[_symbol] = _feedAddress;
    }
    
    function registerTrigger(
        bytes32 _triggerId,
        address _bondContract,
        string memory _equitySymbol,
        uint256 _triggerPrice,
        uint256 _consecutiveDays,
        uint256 _observationPeriod
    ) external onlyRole(ORACLE_ROLE) {
        require(triggers[_triggerId].bondContract == address(0), "Trigger exists");
        
        triggers[_triggerId] = PriceTrigger({
            triggerId: _triggerId,
            bondContract: _bondContract,
            equitySymbol: _equitySymbol,
            triggerPrice: _triggerPrice,
            consecutiveDays: _consecutiveDays,
            observationPeriod: _observationPeriod,
            isActive: true,
            isTriggered: false,
            activationDate: 0
        });
        
        emit TriggerRegistered(_triggerId, _bondContract, _equitySymbol);
    }
    
    function recordObservation(
        bytes32 _triggerId,
        uint256 _price
    ) external onlyRole(ORACLE_ROLE) {
        PriceTrigger storage trigger = triggers[_triggerId];
        require(trigger.isActive, "Trigger not active");
        require(!trigger.isTriggered, "Already triggered");
        
        bool meetsThreshold = _price >= trigger.triggerPrice;
        
        observations[_triggerId].push(PriceObservation({
            timestamp: block.timestamp,
            price: _price,
            meetsThreshold: meetsThreshold
        }));
        
        emit ObservationRecorded(_triggerId, _price, meetsThreshold);
        
        // Check if trigger condition met
        _evaluateTrigger(_triggerId);
    }
    
    function _evaluateTrigger(bytes32 _triggerId) internal {
        PriceTrigger storage trigger = triggers[_triggerId];
        PriceObservation[] storage obs = observations[_triggerId];
        
        if (obs.length < trigger.observationPeriod) {
            return; // Not enough observations
        }
        
        // Check recent observations
        uint256 startIndex = obs.length > trigger.observationPeriod 
            ? obs.length - trigger.observationPeriod 
            : 0;
        
        uint256 consecutiveCount = 0;
        uint256 maxConsecutive = 0;
        
        for (uint256 i = startIndex; i < obs.length; i++) {
            if (obs[i].meetsThreshold) {
                consecutiveCount++;
                if (consecutiveCount > maxConsecutive) {
                    maxConsecutive = consecutiveCount;
                }
            } else {
                consecutiveCount = 0;
            }
        }
        
        if (maxConsecutive >= trigger.consecutiveDays) {
            trigger.isTriggered = true;
            trigger.isActive = false;
            trigger.activationDate = block.timestamp;
            
            emit TriggerActivated(_triggerId, trigger.bondContract);
        }
    }
    
    function getTriggerStatus(bytes32 _triggerId)
        external
        view
        returns (PriceTrigger memory)
    {
        return triggers[_triggerId];
    }
    
    function getRecentObservations(bytes32 _triggerId, uint256 _count)
        external
        view
        returns (bool[] memory)
    {
        PriceObservation[] storage obs = observations[_triggerId];
        uint256 length = obs.length < _count ? obs.length : _count;
        bool[] memory recent = new bool[](length);
        
        uint256 startIndex = obs.length > _count ? obs.length - _count : 0;
        
        for (uint256 i = 0; i < length; i++) {
            recent[i] = obs[startIndex + i].meetsThreshold;
        }
        
        return recent;
    }
}
```

### B. Conversion Execution (Physical, Cash, Net Share Settlement)

**Off-Chain (C# API):**

```csharp
// Service Layer (continued)
public class ConversionExecutionService
{
    private readonly ApplicationDbContext _context;
    private readonly IChainlinkCREService _chainlinkService;
    private readonly ILogger<ConversionExecutionService> _logger;
    
    public async Task<ConversionEvent> RequestConversion(
        Guid bondInstanceId,
        string holderAddress,
        decimal bondAmount,
        SettlementMethod settlementMethod,
        bool isInduced = false,
        decimal inducedConsideration = 0)
    {
        var bond = await _context.BondInstances
            .Include(b => b.ConvertibleParameters)
            .FirstOrDefaultAsync(b => b.Id == bondInstanceId);
        
        var conversionParams = bond.ConvertibleParameters;
        
        // Calculate shares receivable
        var sharesReceivable = CalculateSharesReceivable(
            bondAmount,
            conversionParams.ConversionRatio
        );
        
        decimal? cashAmount = null;
        if (settlementMethod == SettlementMethod.CashSettlement)
        {
            // Full cash settlement
            var stockPrice = await FetchCurrentStockPrice(bond.UnderlyingEquitySymbol);
            cashAmount = sharesReceivable * stockPrice;
        }
        else if (settlementMethod == SettlementMethod.NetShareSettlement)
        {
            // Principal in cash, excess in shares
            cashAmount = bondAmount; // Return principal
            var excessValue = (sharesReceivable * await FetchCurrentStockPrice(bond.UnderlyingEquitySymbol)) - bondAmount;
            sharesReceivable = excessValue / await FetchCurrentStockPrice(bond.UnderlyingEquitySymbol);
        }
        
        var conversionEvent = new ConversionEvent
        {
            Id = Guid.NewGuid(),
            BondInstanceId = bondInstanceId,
            Type = isInduced ? ConversionType.InducedConversion : ConversionType.Voluntary,
            SettlementMethod = settlementMethod,
            HolderAddress = holderAddress,
            BondAmount = bondAmount,
            ConversionRatio = conversionParams.ConversionRatio,
            SharesReceivable = sharesReceivable,
            CashAmount = cashAmount,
            Status = ConversionStatus.Requested,
            RequestedDate = DateTime.UtcNow,
            IsInducedConversion = isInduced,
            InducedConsideration = isInduced ? inducedConsideration : null
        };
        
        _context.ConversionEvents.Add(conversionEvent);
        await _context.SaveChangesAsync();
        
        // Validate conversion eligibility
        await ValidateConversion(conversionEvent);
        
        return conversionEvent;
    }
    
    private decimal CalculateSharesReceivable(decimal bondAmount, decimal conversionRatio)
    {
        // Conversion ratio is shares per $1,000 principal
        return (bondAmount / 1000m) * conversionRatio;
    }
    
    private async Task<decimal> FetchCurrentStockPrice(string symbol)
    {
        var workflowRequest = new ChainlinkWorkflowRequest
        {
            WorkflowId = "fetch-stock-price",
            Parameters = new Dictionary<string, object>
            {
                ["symbol"] = symbol
            }
        };
        
        var response = await _chainlinkService.ExecuteWorkflow(workflowRequest);
        return Convert.ToDecimal(response.Data["price"]);
    }
    
    private async Task ValidateConversion(ConversionEvent conversionEvent)
    {
        var bond = await _context.BondInstances
            .FindAsync(conversionEvent.BondInstanceId);
        
        // Check holder balance on-chain
        var workflowRequest = new ChainlinkWorkflowRequest
        {
            WorkflowId = "validate-conversion",
            Parameters = new Dictionary<string, object>
            {
                ["bondContract"] = bond.ContractAddress,
                ["holderAddress"] = conversionEvent.HolderAddress,
                ["bondAmount"] = conversionEvent.BondAmount
            }
        };
        
        var response = await _chainlinkService.ExecuteWorkflow(workflowRequest);
        var isValid = (bool)response.Data["isValid"];
        
        if (isValid)
        {
            conversionEvent.Status = ConversionStatus.Validated;
            await _context.SaveChangesAsync();
            
            // Proceed to execution
            await ExecuteConversion(conversionEvent.Id);
        }
        else
        {
            conversionEvent.Status = ConversionStatus.Failed;
            await _context.SaveChangesAsync();
            throw new InvalidOperationException("Conversion validation failed");
        }
    }
    
    public async Task ExecuteConversion(Guid conversionEventId)
    {
        var conversionEvent = await _context.ConversionEvents
            .Include(c => c.BondInstance)
            .FirstOrDefaultAsync(c => c.Id == conversionEventId);
        
        if (conversionEvent.Status != ConversionStatus.Validated)
            throw new InvalidOperationException("Conversion not validated");
        
        conversionEvent.Status = ConversionStatus.InProgress;
        await _context.SaveChangesAsync();
        
        try
        {
            var txHash = await ExecuteConversionOnChain(conversionEvent);
            
            conversionEvent.Status = ConversionStatus.Completed;
            conversionEvent.ExecutedDate = DateTime.UtcNow;
            conversionEvent.TransactionHash = txHash;
            await _context.SaveChangesAsync();
            
            // Record accounting entries for liability derecognition
            await RecordLiabilityDerecognition(conversionEvent);
            
            _logger.LogInformation(
                $"Conversion {conversionEvent.Id} completed. TxHash: {txHash}"
            );
        }
        catch (Exception ex)
        {
            conversionEvent.Status = ConversionStatus.Failed;
            await _context.SaveChangesAsync();
            
            _logger.LogError(ex, $"Failed to execute conversion {conversionEvent.Id}");
            throw;
        }
    }
    
    private async Task<string> ExecuteConversionOnChain(ConversionEvent conversionEvent)
    {
        var workflowRequest = new ChainlinkWorkflowRequest
        {
            WorkflowId = "execute-conversion",
            Parameters = new Dictionary<string, object>
            {
                ["bondContract"] = conversionEvent.BondInstance.ContractAddress,
                ["holderAddress"] = conversionEvent.HolderAddress,
                ["bondAmount"] = conversionEvent.BondAmount,
                ["settlementMethod"] = conversionEvent.SettlementMethod.ToString(),
                ["sharesReceivable"] = conversionEvent.SharesReceivable,
                ["cashAmount"] = conversionEvent.CashAmount ?? 0,
                ["isInduced"] = conversionEvent.IsInducedConversion,
                ["inducedConsideration"] = conversionEvent.InducedConsideration ?? 0
            }
        };
        
        var response = await _chainlinkService.ExecuteWorkflow(workflowRequest);
        return response.TransactionHash;
    }
    
    private async Task RecordLiabilityDerecognition(ConversionEvent conversionEvent)
    {
        // Get bifurcated accounting
        var bifurcation = await _context.BifurcatedAccounting
            .FirstOrDefaultAsync(b => b.BondInstanceId == conversionEvent.BondInstanceId);
        
        // Calculate carrying amount being converted
        var bond = conversionEvent.BondInstance;
        var conversionPercentage = conversionEvent.BondAmount / bond.TotalIssuanceSize;
        var carryingAmountConverted = bifurcation.LiabilityComponent * conversionPercentage;
        
        // Create journal entries
        // Debit: Convertible Bonds Payable (liability component)
        // Debit: APIC - Convertible Bonds (equity component)
        // Credit: Common Stock (par value)
        // Credit: APIC - Common Stock (excess)
        
        var parValue = 0.01m; // Assuming $0.01 par value
        var commonStockValue = conversionEvent.SharesReceivable * parValue;
        var apicValue = carryingAmountConverted - commonStockValue;
        
        var entries = new List<AccountingEntry>
        {
            new AccountingEntry
            {
                Id = Guid.NewGuid(),
                BifurcatedAccountingId = bifurcation.Id,
                EntryDate = DateTime.UtcNow,
                AccountType = "Convertible Bonds Payable",
                DebitAmount = carryingAmountConverted,
                CreditAmount = 0,
                Description = $"Derecognition of liability - Conversion {conversionEvent.Id}"
            },
            new AccountingEntry
            {
                Id = Guid.NewGuid(),
                BifurcatedAccountingId = bifurcation.Id,
                EntryDate = DateTime.UtcNow,
                AccountType = "APIC - Convertible Bonds",
                DebitAmount = bifurcation.EquityComponent * conversionPercentage,
                CreditAmount = 0,
                Description = $"Transfer of equity component - Conversion {conversionEvent.Id}"
            },
            new AccountingEntry
            {
                Id = Guid.NewGuid(),
                BifurcatedAccountingId = bifurcation.Id,
                EntryDate = DateTime.UtcNow,
                AccountType = "Common Stock",
                DebitAmount = 0,
                CreditAmount = commonStockValue,
                Description = $"Issuance of {conversionEvent.SharesReceivable} shares at par"
            },
            new AccountingEntry
            {
                Id = Guid.NewGuid(),
                BifurcatedAccountingId = bifurcation.Id,
                EntryDate = DateTime.UtcNow,
                AccountType = "APIC - Common Stock",
                DebitAmount = 0,
                CreditAmount = apicValue,
                Description = $"Excess over par value - Conversion {conversionEvent.Id}"
            }
        };
        
        // Handle induced conversion incremental cost
        if (conversionEvent.IsInducedConversion && conversionEvent.InducedConsideration > 0)
        {
            entries.Add(new AccountingEntry
            {
                Id = Guid.NewGuid(),
                BifurcatedAccountingId = bifurcation.Id,
                EntryDate = DateTime.UtcNow,
                AccountType = "Induced Conversion Expense",
                DebitAmount = conversionEvent.InducedConsideration.Value,
                CreditAmount = 0,
                Description = $"Incremental cost of induced conversion"
            });
            
            entries.Add(new AccountingEntry
            {
                Id = Guid.NewGuid(),
                BifurcatedAccountingId = bifurcation.Id,
                EntryDate = DateTime.UtcNow,
                AccountType = "Cash/Additional Consideration",
                DebitAmount = 0,
                CreditAmount = conversionEvent.InducedConsideration.Value,
                Description = $"Payment of induced conversion consideration"
            });
        }
        
        _context.AccountingEntries.AddRange(entries);
        await _context.SaveChangesAsync();
    }
}
```

**On-Chain Smart Contracts:**

```solidity
// ConversionManager.sol (extended)
pragma solidity ^0.8.20;

import "@openzeppelin/contracts/token/ERC20/IERC20.sol";
import "@openzeppelin/contracts/access/AccessControl.sol";
import "@openzeppelin/contracts/security/ReentrancyGuard.sol";

contract ConversionManagerExtended is AccessControl, ReentrancyGuard {
    bytes32 public constant ORACLE_ROLE = keccak256("ORACLE_ROLE");
    
    enum SettlementMethod {
        PhysicalSettlement,
        CashSettlement,
        NetShareSettlement
    }
    
    struct ConversionRequest {
        address holder;
        uint256 bondAmount;
        uint256 sharesReceivable;
        uint256 cashAmount;
        SettlementMethod settlementMethod;
        bool isInduced;
        uint256 inducedConsideration;
        uint256 requestTimestamp;
        bool isExecuted;
    }
    
    IERC20 public bondToken;
    IERC20 public equityToken;
    IERC20 public cashToken; // e.g., USDC
    
    mapping(bytes32 => ConversionRequest) public conversionRequests;
    mapping(address => uint256) public totalConversions;
    
    event ConversionRequested(
        bytes32 indexed requestId,
        address indexed holder,
        uint256 bondAmount,
        SettlementMethod settlementMethod
    );
    
    event ConversionExecuted(
        bytes32 indexed requestId,
        address indexed holder,
        uint256 bondAmount,
        uint256 sharesIssued,
        uint256 cashPaid
    );
    
    event InducedConversionExecuted(
        bytes32 indexed requestId,
        address indexed holder,
        uint256 inducedConsideration
    );
    
    constructor(
        address _bondToken,
        address _equityToken,
        address _cashToken
    ) {
        bondToken = IERC20(_bondToken);
        equityToken = IERC20(_equityToken);
        cashToken = IERC20(_cashToken);
        _grantRole(DEFAULT_ADMIN_ROLE, msg.sender);
    }
    
    function executeConversion(
        bytes32 _requestId,
        address _holder,
        uint256 _bondAmount,
        uint256 _sharesReceivable,
        uint256 _cashAmount,
        SettlementMethod _settlementMethod,
        bool _isInduced,
        uint256 _inducedConsideration
    ) external onlyRole(ORACLE_ROLE) nonReentrant {
        require(
            conversionRequests[_requestId].holder == address(0),
            "Request already exists"
        );
        
        // Record request
        conversionRequests[_requestId] = ConversionRequest({
            holder: _holder,
            bondAmount: _bondAmount,
            sharesReceivable: _sharesReceivable,
            cashAmount: _cashAmount,
            settlementMethod: _settlementMethod,
            isInduced: _isInduced,
            inducedConsideration: _inducedConsideration,
            requestTimestamp: block.timestamp,
            isExecuted: false
        });
        
        emit ConversionRequested(
            _requestId,
            _holder,
            _bondAmount,
            _settlementMethod
        );
        
        // Execute conversion
        _executeConversionSettlement(_requestId);
    }
    
    function _executeConversionSettlement(bytes32 _requestId) internal {
        ConversionRequest storage request = conversionRequests[_requestId];
        
        // Burn bond tokens
        bondToken.transferFrom(
            request.holder,
            address(this),
            request.bondAmount
        );
        // Actual burn would happen here in production
        
        uint256 sharesIssued = 0;
        uint256 cashPaid = 0;
        
        if (request.settlementMethod == SettlementMethod.PhysicalSettlement) {
            // Issue equity tokens only
            require(
                equityToken.transfer(request.holder, request.sharesReceivable),
                "Equity transfer failed"
            );
            sharesIssued = request.sharesReceivable;
            
        } else if (request.settlementMethod == SettlementMethod.CashSettlement) {
            // Pay cash only
            require(
                cashToken.transfer(request.holder, request.cashAmount),
                "Cash transfer failed"
            );
            cashPaid = request.cashAmount;
            
        } else if (request.settlementMethod == SettlementMethod.NetShareSettlement) {
            // Pay principal in cash
            require(
                cashToken.transfer(request.holder, request.bondAmount),
                "Cash transfer failed"
            );
            cashPaid = request.bondAmount;
            
            // Issue shares for excess value
            if (request.sharesReceivable > 0) {
                require(
                    equityToken.transfer(request.holder, request.sharesReceivable),
                    "Equity transfer failed"
                );
                sharesIssued = request.sharesReceivable;
            }
        }
        
        // Handle induced conversion consideration
        if (request.isInduced && request.inducedConsideration > 0) {
            require(
                cashToken.transfer(request.holder, request.inducedConsideration),
                "Induced consideration transfer failed"
            );
            
            emit InducedConversionExecuted(
                _requestId,
                request.holder,
                request.inducedConsideration
            );
        }
        
        request.isExecuted = true;
        totalConversions[request.holder] += request.bondAmount;
        
        emit ConversionExecuted(
            _requestId,
            request.holder,
            request.bondAmount,
            sharesIssued,
            cashPaid
        );
    }
    
    function getConversionRequest(bytes32 _requestId)
        external
        view
        returns (ConversionRequest memory)
    {
        return conversionRequests[_requestId];
    }
}
```

---

## System Integration Flow Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                         USER INTERACTION                         │
└────────────┬────────────────────────────────────────────────────┘
             │
             ▼
┌─────────────────────────────────────────────────────────────────┐
│                    C# ASP.NET WEB API                            │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Security Master Service                                  │   │
│  │  - Create bond instances                                  │   │
│  │  - Link equity components                                 │   │
│  └────────────┬─────────────────────────────────────────────┘   │
│               │                                                   │
│  ┌────────────▼─────────────────────────────────────────────┐   │
│  │  Convertible Parameter Service                            │   │
│  │  - Set/update conversion parameters                       │   │
│  │  - Calculate conversion ratios                            │   │
│  └────────────┬─────────────────────────────────────────────┘   │
│               │                                                   │
│  ┌────────────▼─────────────────────────────────────────────┐   │
│  │  Bifurcated Accounting Service                            │   │
│  │  - Separate liability/equity components                   │   │
│  │  - Calculate effective interest rate                      │   │
│  └────────────┬─────────────────────────────────────────────┘   │
│               │                                                   │
│  ┌────────────▼─────────────────────────────────────────────┐   │
│  │  Amortization Service                                     │   │
│  │  - Generate amortization schedule                         │   │
│  │  - Auto-post periodic entries                             │   │
│  └────────────┬─────────────────────────────────────────────┘   │
│               │                                                   │
│  ┌────────────▼─────────────────────────────────────────────┐   │
│  │  Coupon Payment Service                                   │   │
│  │  - Create payment schedules                               │   │
│  │  - Process record dates                                   │   │
│  │  - Execute automated payments                             │   │
│  └────────────┬─────────────────────────────────────────────┘   │
│               │                                                   │
│  ┌────────────▼─────────────────────────────────────────────┐   │
│  │  Conversion Monitor Service                               │   │
│  │  - Monitor contingent triggers                            │   │
│  │  - Track price observations                               │   │
│  └────────────┬─────────────────────────────────────────────┘   │
│               │                                                   │
│  ┌────────────▼─────────────────────────────────────────────┐   │
│  │  Conversion Execution Service                             │   │
│  │  - Process conversion requests                            │   │
│  │  - Execute settlements                                    │   │
│  │  - Record liability derecognition                         │   │
│  └────────────┬─────────────────────────────────────────────┘   │
│               │                                                   │
│  ┌────────────▼─────────────────────────────────────────────┐   │
│  │  Chainlink CRE Communication Layer                        │   │
│  │  - Execute workflows                                      │   │
│  │  - Handle DON responses                                   │   │
│  └────────────┬─────────────────────────────────────────────┘   │
└───────────────┼─────────────────────────────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────────────────────────────┐
│                    CHAINLINK DON (CRE)                           │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Workflow Orchestration                                   │   │
│  │  - Coordinate on-chain/off-chain operations              │   │
│  │  - Execute Functions code                                 │   │
│  │  - Manage state transitions                               │   │
│  └────────────┬─────────────────────────────────────────────┘   │
│               │                                                   │
│  ┌────────────▼─────────────────────────────────────────────┐   │
│  │  Price Oracle Integration                                 │   │
│  │  - Fetch stock prices                                     │   │
│  │  - Aggregate from multiple sources                        │   │
│  └────────────┬─────────────────────────────────────────────┘   │
│               │                                                   │
│  ┌────────────▼─────────────────────────────────────────────┐   │
│  │  Automation Functions                                     │   │
│  │  - Scheduled trigger checks                               │   │
│  │  - Automated payment processing                           │   │
│  └────────────┬─────────────────────────────────────────────┘   │
└───────────────┼─────────────────────────────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────────────────────────────┐
│                     BLOCKCHAIN LAYER                             │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Bond Token Contract (ERC-20)                            │   │
│  │  - Stores bond master data                                │   │
│  │  - Manages token balances                                 │   │
│  └──────────────────────────────────────────────────────────┘   │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Conversion Manager Contract                              │   │
│  │  - Stores conversion parameters                           │   │
│  │  - Executes conversions                                   │   │
│  └──────────────────────────────────────────────────────────┘   │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Accounting Ledger Contract                               │   │
│  │  - Records bifurcated components                          │   │
│  │  - Maintains journal entries                              │   │
│  └──────────────────────────────────────────────────────────┘   │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Amortization Contract                                    │   │
│  │  - Stores amortization periods                            │   │
│  │  - Updates carrying amounts                               │   │
│  └──────────────────────────────────────────────────────────┘   │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Coupon Distributor Contract                              │   │
│  │  - Executes batch payments                                │   │
│  │  - Records payment history                                │   │
│  └──────────────────────────────────────────────────────────┘   │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Oracle Integration Contract                              │   │
│  │  - Monitors price triggers                                │   │
│  │  - Stores observations                                    │   │
│  │  - Activates triggers                                     │   │
│  └──────────────────────────────────────────────────────────┘   │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Equity Token Contract (ERC-20)                          │   │
│  │  - Manages equity token issuance                          │   │
│  │  - Handles conversion settlements                         │   │
│  └──────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

## Database Schema (Entity Framework)

```csharp
public class ApplicationDbContext : DbContext
{
    // Security Master
    public DbSet<BondInstance> BondInstances { get; set; }
    public DbSet<EquityComponent> EquityComponents { get; set; }
    
    // Convertible Parameters
    public DbSet<ConvertibleParameters> ConvertibleParameters { get; set; }
    public DbSet<ConversionParameterHistory> ConversionParameterHistory { get; set; }
    
    // Bifurcated Accounting
    public DbSet<BifurcatedAccounting> BifurcatedAccounting { get; set; }
    public DbSet<AccountingEntry> AccountingEntries { get; set; }
    
    // Amortization
    public DbSet<AmortizationSchedule> AmortizationSchedules { get; set; }
    public DbSet<AmortizationPeriod> AmortizationPeriods { get; set; }
    
    // Coupon Payments
    public DbSet<CouponPaymentSchedule> CouponPaymentSchedules { get; set; }
    public DbSet<CouponPayment> CouponPayments { get; set; }
    public DbSet<CouponPaymentDetail> CouponPaymentDetails { get; set; }
    
    // Conversion
    public DbSet<ContingentTrigger> ContingentTriggers { get; set; }
    public DbSet<TriggerObservation> TriggerObservations { get; set; }
    public DbSet<ConversionEvent> ConversionEvents { get; set; }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configure relationships and indexes
        modelBuilder.Entity<BondInstance>()
            .HasOne(b => b.BifurcatedAccounting)
            .WithOne(ba => ba.BondInstance)
            .HasForeignKey<BifurcatedAccounting>(ba => ba.BondInstanceId);
        
        modelBuilder.Entity<AmortizationSchedule>()
            .HasMany(s => s.Periods)
            .WithOne()
            .HasForeignKey(p => p.AmortizationScheduleId);
        
        // Indexes for performance
        modelBuilder.Entity<CouponPayment>()
            .HasIndex(cp => new { cp.Status, cp.PaymentDate });
        
        modelBuilder.Entity<ContingentTrigger>()
            .HasIndex(ct => new { ct.IsActive, ct.BondInstanceId });
    }
}
```

This comprehensive architecture provides a complete tokenization platform for convertible bonds with full integration between off-chain C# systems, Chainlink CRE for orchestration, and on-chain smart contracts for transparent, immutable record-keeping.