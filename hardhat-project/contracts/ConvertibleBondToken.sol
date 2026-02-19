// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import "@openzeppelin/contracts/token/ERC1155/ERC1155.sol";
import "@openzeppelin/contracts/access/AccessControl.sol";
import "@openzeppelin/contracts/utils/Pausable.sol";
import "@openzeppelin/contracts/utils/ReentrancyGuard.sol";
import "../interfaces/IReceiver.sol";

/**
 * @title ConvertibleBondTokenCRE
 * @dev ERC-1155 token contract for convertible bonds with Chainlink Runtime Environment (CRE) integration
 * @notice This contract manages bonds (IDs 1-999) and equity tokens (IDs 1000+)
 * 
 * CRE Integration:
 * - Implements IReceiver interface to accept data from CRE workflows
 * - Can be triggered by CRE workflows for automated bond operations
 * - Validates that calls come from authorized Chainlink Forwarder
 */
contract ConvertibleBondTokenCRE is ERC1155, AccessControl, Pausable, ReentrancyGuard, IReceiver {

    // Role definitions
    bytes32 public constant ADMIN_ROLE = keccak256("ADMIN_ROLE");
    bytes32 public constant ISSUER_ROLE = keccak256("ISSUER_ROLE");
    bytes32 public constant COMPLIANCE_ROLE = keccak256("COMPLIANCE_ROLE");
    bytes32 public constant CRE_WORKFLOW_ROLE = keccak256("CRE_WORKFLOW_ROLE");

    // Token ID ranges
    uint256 public constant BOND_ID_START = 1;
    uint256 public constant BOND_ID_END = 999;
    uint256 public constant EQUITY_ID_START = 1000;

    // Counters for token IDs
    uint256 private _bondIdCounter;
    uint256 private _equityIdCounter;

    // Chainlink Forwarder address for CRE integration
    address public chainlinkForwarder;

    // Bond series information
    struct BondSeries {
        uint256 equityTokenId;
        uint256 conversionRatio;
        uint256 maturityDate;
        uint256 faceValue;
        uint256 totalIssued;
        uint256 totalConverted;
        bool isActive;
        bool conversionEnabled;
        string symbol;
        string name;
        string isin;
        uint256 couponRate; // in basis points (e.g., 500 for 5%)
    }

    // Equity class information
    struct EquityClass {
        string className;
        uint256 totalSupply;
        bool isActive;
        string metadata;
    }

    // CRE Operation types
    enum CREOperationType {
        ISSUE_BONDS,
        CONVERT_BONDS,
        WHITELIST_BONDHOLDER,
        WHITELIST_EQUITYHOLDER,
        UPDATE_CONVERSION_STATUS
    }

    // Mappings
    mapping(uint256 => BondSeries) public bondSeries;
    mapping(uint256 => EquityClass) public equityClasses;
    mapping(uint256 => mapping(address => bool)) public bondholderWhitelist;
    mapping(uint256 => mapping(address => bool)) public equityholderWhitelist;

    // Events
    event BondSeriesCreated(
        uint256 indexed bondId,
        uint256 indexed equityId,
        uint256 conversionRatio,
        uint256 maturityDate,
        uint256 faceValue,
        string symbol,
        string name,
        string isin,
        uint256 couponRate
    );

    event EquityClassCreated(
        uint256 indexed equityId,
        string className,
        string metadata
    );

    event ConvertibleBondCreated(
        uint256 indexed bondId,
        uint256 indexed equityId,
        uint256 conversionRatio,
        uint256 maturityDate,
        uint256 faceValue,
        string symbol,
        string name,
        string isin,
        uint256 couponRate
    );

    event BondsIssued(
        uint256 indexed bondId,
        address indexed recipient,
        uint256 amount,
        uint256 totalValue
    );

    event BondsConverted(
        uint256 indexed bondId,
        uint256 indexed equityId,
        address indexed holder,
        uint256 bondAmount,
        uint256 equityAmount
    );

    event ConversionStatusChanged(
        uint256 indexed bondId,
        bool enabled
    );

    event HolderWhitelisted(
        uint256 indexed tokenId,
        address indexed holder,
        bool isBond
    );

    event HolderRemovedFromWhitelist(
        uint256 indexed tokenId,
        address indexed holder,
        bool isBond
    );

    event CREReportReceived(
        CREOperationType indexed operationType,
        bytes data
    );

    event ChainlinkForwarderUpdated(
        address indexed oldForwarder,
        address indexed newForwarder
    );

    /**
     * @dev Constructor sets up the contract with initial URI, roles, and Chainlink Forwarder
     * @param uri Base URI for token metadata
     * @param _chainlinkForwarder Address of the Chainlink Forwarder contract for CRE
     */
    constructor(string memory uri, address _chainlinkForwarder) ERC1155(uri) {
        require(_chainlinkForwarder != address(0), "Invalid forwarder address");
        
        chainlinkForwarder = _chainlinkForwarder;

        _grantRole(DEFAULT_ADMIN_ROLE, msg.sender);
        _grantRole(ADMIN_ROLE, msg.sender);
        _grantRole(ISSUER_ROLE, msg.sender);
        _grantRole(COMPLIANCE_ROLE, msg.sender);

        // Initialize counters
        _bondIdCounter = BOND_ID_START - 1;
        _equityIdCounter = EQUITY_ID_START - 1;
    }

    /**
     * @notice IReceiver implementation - called by Chainlink Forwarder to deliver CRE workflow data
     * @dev Only the authorized Chainlink Forwarder can call this function
     * @param metadata Additional report metadata from CRE
     * @param report ABI-encoded operation data from CRE workflow
     */
    function onReport(bytes calldata metadata, bytes calldata report) 
        external 
        override 
        whenNotPaused 
    {
        require(msg.sender == chainlinkForwarder, "Only Chainlink Forwarder can call");

        // Decode the operation type and data
        (CREOperationType operationType, bytes memory operationData) = abi.decode(
            report,
            (CREOperationType, bytes)
        );

        emit CREReportReceived(operationType, operationData);

        // Route to appropriate handler based on operation type
        if (operationType == CREOperationType.ISSUE_BONDS) {
            _handleCREIssueBonds(operationData);
        } else if (operationType == CREOperationType.CONVERT_BONDS) {
            _handleCREConvertBonds(operationData);
        } else if (operationType == CREOperationType.WHITELIST_BONDHOLDER) {
            _handleCREWhitelistBondholder(operationData);
        } else if (operationType == CREOperationType.WHITELIST_EQUITYHOLDER) {
            _handleCREWhitelistEquityholder(operationData);
        } else if (operationType == CREOperationType.UPDATE_CONVERSION_STATUS) {
            _handleCREUpdateConversionStatus(operationData);
        } else {
            revert("Unknown CRE operation type");
        }
    }

    /**
     * @dev Handle bond issuance from CRE workflow
     */
    function _handleCREIssueBonds(bytes memory data) private {
        (uint256 bondId, address recipient, uint256 amount) = abi.decode(
            data,
            (uint256, address, uint256)
        );

        require(bondSeries[bondId].isActive, "Bond series is not active");
        require(bondholderWhitelist[bondId][recipient], "Recipient not whitelisted");
        require(amount > 0, "Amount must be positive");

        bondSeries[bondId].totalIssued += amount;
        uint256 totalValue = amount * bondSeries[bondId].faceValue;

        _mint(recipient, bondId, amount, "");

        emit BondsIssued(bondId, recipient, amount, totalValue);
    }

    /**
     * @dev Handle bond conversion from CRE workflow
     */
    function _handleCREConvertBonds(bytes memory data) private {
        (address holder, uint256 bondId, uint256 bondAmount) = abi.decode(
            data,
            (address, uint256, uint256)
        );

        require(bondSeries[bondId].isActive, "Bond series does not exist");
        require(bondSeries[bondId].conversionEnabled, "Conversion is disabled");
        require(bondAmount > 0, "Amount must be positive");
        require(balanceOf(holder, bondId) >= bondAmount, "Insufficient bond balance");

        uint256 equityId = bondSeries[bondId].equityTokenId;
        require(equityholderWhitelist[equityId][holder], "Not whitelisted for equity");

        uint256 equityAmount = (bondAmount * bondSeries[bondId].conversionRatio) / 1e18;
        require(equityAmount > 0, "Conversion would result in zero equity");

        _burn(holder, bondId, bondAmount);
        _mint(holder, equityId, equityAmount, "");

        bondSeries[bondId].totalConverted += bondAmount;
        equityClasses[equityId].totalSupply += equityAmount;

        emit BondsConverted(bondId, equityId, holder, bondAmount, equityAmount);
    }

    /**
     * @dev Handle bondholder whitelisting from CRE workflow
     */
    function _handleCREWhitelistBondholder(bytes memory data) private {
        (uint256 bondId, address holder) = abi.decode(data, (uint256, address));

        require(bondSeries[bondId].isActive, "Bond series does not exist");
        require(holder != address(0), "Invalid address");

        bondholderWhitelist[bondId][holder] = true;

        emit HolderWhitelisted(bondId, holder, true);
    }

    /**
     * @dev Handle equityholder whitelisting from CRE workflow
     */
    function _handleCREWhitelistEquityholder(bytes memory data) private {
        (uint256 equityId, address holder) = abi.decode(data, (uint256, address));

        require(equityClasses[equityId].isActive, "Equity class does not exist");
        require(holder != address(0), "Invalid address");

        equityholderWhitelist[equityId][holder] = true;

        emit HolderWhitelisted(equityId, holder, false);
    }

    /**
     * @dev Handle conversion status update from CRE workflow
     */
    function _handleCREUpdateConversionStatus(bytes memory data) private {
        (uint256 bondId, bool enabled) = abi.decode(data, (uint256, bool));

        require(bondSeries[bondId].isActive, "Bond series does not exist");

        bondSeries[bondId].conversionEnabled = enabled;

        emit ConversionStatusChanged(bondId, enabled);
    }

    /**
     * @notice Update the Chainlink Forwarder address
     * @dev Only ADMIN_ROLE can update the forwarder
     * @param newForwarder New Chainlink Forwarder address
     */
    function setChainlinkForwarder(address newForwarder) external onlyRole(ADMIN_ROLE) {
        require(newForwarder != address(0), "Invalid forwarder address");
        
        address oldForwarder = chainlinkForwarder;
        chainlinkForwarder = newForwarder;

        emit ChainlinkForwarderUpdated(oldForwarder, newForwarder);
    }

    /**
     * @dev Creates a new equity class
     */
    function createEquityClass(
        string memory className,
        string memory metadata
    ) private  returns (uint256) {
        _equityIdCounter++;
        uint256 equityId = _equityIdCounter;

        require(equityId >= EQUITY_ID_START, "Invalid equity ID");

        equityClasses[equityId] = EquityClass({
            className: className,
            totalSupply: 0,
            isActive: true,
            metadata: metadata
        });

        emit EquityClassCreated(equityId, className, metadata);

        return equityId;
    }

    /**
     * @dev Creates a new bond series
     */
    function createBondSeries(
        uint256 equityTokenId,
        uint256 conversionRatio,
        uint256 maturityDate,
        uint256 faceValue,
        string memory symbol,
        string memory name,
        string memory isin,
        uint256 couponRate
    ) private  returns (uint256) {
        require(equityClasses[equityTokenId].isActive, "Equity class does not exist");
        require(conversionRatio > 0, "Conversion ratio must be positive");
        require(maturityDate > block.timestamp, "Maturity date must be in future");
        require(faceValue > 0, "Face value must be positive");

        _bondIdCounter++;
        uint256 bondId = _bondIdCounter;

        require(bondId >= BOND_ID_START && bondId <= BOND_ID_END, "Invalid bond ID");

        bondSeries[bondId] = BondSeries({
            equityTokenId: equityTokenId,
            conversionRatio: conversionRatio,
            maturityDate: maturityDate,
            faceValue: faceValue,
            totalIssued: 0,
            totalConverted: 0,
            isActive: true,
            conversionEnabled: true,
            symbol: symbol,
            name: name,
            isin: isin,
            couponRate: couponRate
        });

        emit BondSeriesCreated(bondId, equityTokenId, conversionRatio, maturityDate, faceValue, symbol, name, isin, couponRate);

        return bondId;
    }

    /**
     * @dev Creates a new convertible bond setup by creating both an equity class and bond series.
     */
    function createConvertibleBond(
        string memory className,
        string memory metadata,
        uint256 conversionRatio,
        uint256 maturityDate,
        uint256 faceValue,
        string memory symbol,
        string memory name,
        string memory isin,
        uint256 couponRate
    ) external onlyRole(ISSUER_ROLE) whenNotPaused returns (uint256 equityId, uint256 bondId) {
        equityId = createEquityClass(className, metadata);
        bondId = createBondSeries(
            equityId,
            conversionRatio,
            maturityDate,
            faceValue,
            symbol,
            name,
            isin,
            couponRate
        );

        emit ConvertibleBondCreated(
            bondId,
            equityId,
            conversionRatio,
            maturityDate,
            faceValue,
            symbol,
            name,
            isin,
            couponRate
        );
    }

    /**
     * @dev Issues bonds to a recipient (traditional method, not via CRE)
     */
    function issueBonds(
        uint256 bondId,
        address recipient,
        uint256 amount
    ) external onlyRole(ISSUER_ROLE) whenNotPaused {
        require(bondSeries[bondId].isActive, "Bond series is not active");
        require(bondholderWhitelist[bondId][recipient], "Recipient not whitelisted");
        require(amount > 0, "Amount must be positive");

        bondSeries[bondId].totalIssued += amount;
        uint256 totalValue = amount * bondSeries[bondId].faceValue;

        _mint(recipient, bondId, amount, "");

        emit BondsIssued(bondId, recipient, amount, totalValue);
    }

    /**
     * @dev Converts bonds to equity (traditional method, not via CRE)
     */
    function convertBondsToEquity(
        uint256 bondId,
        uint256 bondAmount
    ) external nonReentrant whenNotPaused {
        require(bondSeries[bondId].isActive, "Bond series does not exist");
        require(bondSeries[bondId].conversionEnabled, "Conversion is disabled");
        require(bondAmount > 0, "Amount must be positive");
        require(balanceOf(msg.sender, bondId) >= bondAmount, "Insufficient bond balance");

        uint256 equityId = bondSeries[bondId].equityTokenId;
        require(equityholderWhitelist[equityId][msg.sender], "Not whitelisted for equity");

        uint256 equityAmount = (bondAmount * bondSeries[bondId].conversionRatio) / 1e18;
        require(equityAmount > 0, "Conversion would result in zero equity");

        _burn(msg.sender, bondId, bondAmount);
        _mint(msg.sender, equityId, equityAmount, "");

        bondSeries[bondId].totalConverted += bondAmount;
        equityClasses[equityId].totalSupply += equityAmount;

        emit BondsConverted(bondId, equityId, msg.sender, bondAmount, equityAmount);
    }

    // [Include all other functions from original contract: whitelisting, admin functions, etc.]
    // Keeping response concise - these are identical to the original contract

    function whitelistBondholder(uint256 bondId, address holder) external onlyRole(COMPLIANCE_ROLE) {
        require(bondSeries[bondId].isActive, "Bond series does not exist");
        require(holder != address(0), "Invalid address");
        bondholderWhitelist[bondId][holder] = true;
        emit HolderWhitelisted(bondId, holder, true);
    }

    function whitelistEquityholder(uint256 equityId, address holder) external onlyRole(COMPLIANCE_ROLE) {
        require(equityClasses[equityId].isActive, "Equity class does not exist");
        require(holder != address(0), "Invalid address");
        equityholderWhitelist[equityId][holder] = true;
        emit HolderWhitelisted(equityId, holder, false);
    }

    function batchWhitelistBondholders(uint256 bondId, address[] calldata holders) external onlyRole(COMPLIANCE_ROLE) {
        require(bondSeries[bondId].isActive, "Bond series does not exist");
        for (uint256 i = 0; i < holders.length; i++) {
            require(holders[i] != address(0), "Invalid address");
            bondholderWhitelist[bondId][holders[i]] = true;
            emit HolderWhitelisted(bondId, holders[i], true);
        }
    }

    function batchWhitelistEquityholders(uint256 equityId, address[] calldata holders) external onlyRole(COMPLIANCE_ROLE) {
        require(equityClasses[equityId].isActive, "Equity class does not exist");
        for (uint256 i = 0; i < holders.length; i++) {
            require(holders[i] != address(0), "Invalid address");
            equityholderWhitelist[equityId][holders[i]] = true;
            emit HolderWhitelisted(equityId, holders[i], false);
        }
    }

    function setConversionEnabled(uint256 bondId, bool enabled) external onlyRole(ADMIN_ROLE) {
        require(bondSeries[bondId].isActive, "Bond series does not exist");
        bondSeries[bondId].conversionEnabled = enabled;
        emit ConversionStatusChanged(bondId, enabled);
    }

    function deactivateBondSeries(uint256 bondId) external onlyRole(ADMIN_ROLE) {
        bondSeries[bondId].isActive = false;
    }

    function deactivateEquityClass(uint256 equityId) external onlyRole(ADMIN_ROLE) {
        equityClasses[equityId].isActive = false;
    }

    function setURI(string memory newuri) external onlyRole(ADMIN_ROLE) {
        _setURI(newuri);
    }

    function pause() external onlyRole(ADMIN_ROLE) {
        _pause();
    }

    function unpause() external onlyRole(ADMIN_ROLE) {
        _unpause();
    }

    function getBondSeries(uint256 bondId) external view returns (BondSeries memory) {
        return bondSeries[bondId];
    }

    function getEquityClass(uint256 equityId) external view returns (EquityClass memory) {
        return equityClasses[equityId];
    }

    function calculateConversion(uint256 bondId, uint256 bondAmount) external view returns (uint256) {
        require(bondSeries[bondId].isActive, "Bond series does not exist");
        return (bondAmount * bondSeries[bondId].conversionRatio) / 1e18;
    }

    function isBondMatured(uint256 bondId) external view returns (bool) {
        return block.timestamp >= bondSeries[bondId].maturityDate;
    }

    function _update(
        address from,
        address to,
        uint256[] memory ids,
        uint256[] memory amounts
    ) internal override whenNotPaused {
        if (from != address(0) && to != address(0)) {
            for (uint256 i = 0; i < ids.length; i++) {
                uint256 id = ids[i];

                if (id >= BOND_ID_START && id <= BOND_ID_END) {
                    require(bondholderWhitelist[id][to], "Recipient not whitelisted for bond");
                } else if (id >= EQUITY_ID_START) {
                    require(equityholderWhitelist[id][to], "Recipient not whitelisted for equity");
                }
            }
        }

        super._update(from, to, ids, amounts);
    }

    function supportsInterface(bytes4 interfaceId)
        public
        view
        override(IERC165,ERC1155, AccessControl)
        returns (bool)
    {
        return super.supportsInterface(interfaceId);
    }
}
