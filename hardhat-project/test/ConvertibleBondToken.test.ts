import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { network } from "hardhat";
import { parseEther, getAddress, zeroAddress, encodeAbiParameters, encodePacked, keccak256, toHex, stringToHex, encodeFunctionData, encodeEventTopics, decodeEventLog } from "viem";

describe("ConvertibleBondTokenCRE", async function () {
    const { viem } = await network.connect();
    const publicClient = await viem.getPublicClient();
    const [deployer, issuer, compliance, user, forwarder] = await viem.getWalletClients();

    const baseURI = "https://api.example.com/metadata/";

    async function deployContract() {
        const contract = await viem.deployContract("ConvertibleBondTokenCRE", [
            baseURI,
            forwarder.account.address,
        ]);
        return contract;
    }

    it("Should set the correct roles upon deployment", async function () {
        const contract = await deployContract();

        const ADMIN_ROLE = await contract.read.ADMIN_ROLE();
        const ISSUER_ROLE = await contract.read.ISSUER_ROLE();
        const COMPLIANCE_ROLE = await contract.read.COMPLIANCE_ROLE();
        const DEFAULT_ADMIN_ROLE = "0x0000000000000000000000000000000000000000000000000000000000000000";

        assert.equal(await contract.read.hasRole([DEFAULT_ADMIN_ROLE, deployer.account.address]), true);
        assert.equal(await contract.read.hasRole([ADMIN_ROLE, deployer.account.address]), true);
        assert.equal(await contract.read.hasRole([ISSUER_ROLE, deployer.account.address]), true);
        assert.equal(await contract.read.hasRole([COMPLIANCE_ROLE, deployer.account.address]), true);
    });

    it("Should allow ISSUER_ROLE to create a convertible bond", async function () {
        const contract = await deployContract();

        const className = "Series A Equity";
        const metadata = "Equity Metadata";
        const conversionRatio = parseEther("100"); // 1 bond = 100 equity
        const maturityDate = BigInt(Math.floor(Date.now() / 1000) + 86400 * 365); // 1 year from now
        const faceValue = parseEther("1000"); // $1000 face value
        const symbol = "CB-XYZ";
        const name = "Convertible Bond XYZ";
        const isin = "US123456789";
        const couponRate = 500n; // 5%

        // We need to use the contract as the deployer (who has ISSUER_ROLE)
        const result = await contract.write.createConvertibleBond([
            className,
            metadata,
            conversionRatio,
            maturityDate,
            faceValue,
            symbol,
            name,
            isin,
            couponRate
        ]);

        // Check equity class creation
        const equityId = 1000n;
        const equityClass = await contract.read.getEquityClass([equityId]);
        assert.equal(equityClass.className, className);
        assert.equal(equityClass.isActive, true);

        // Check bond series creation
        const bondId = 1n;
        const bondSeries = await contract.read.getBondSeries([bondId]);
        assert.equal(bondSeries.symbol, symbol);
        assert.equal(bondSeries.isActive, true);
        assert.equal(bondSeries.equityTokenId, equityId);
    });

    it("Should allow COMPLIANCE_ROLE to whitelist holders", async function () {
        const contract = await deployContract();

        // Setup a bond series first
        await contract.write.createConvertibleBond([
            "Class A", "Meta", parseEther("1"), BigInt(Math.floor(Date.now() / 1000) + 10000),
            parseEther("100"), "SYMB", "Name", "ISIN", 500n
        ]);

        const bondId = 1n;
        const equityId = 1000n;

        await contract.write.whitelistBondholder([bondId, user.account.address]);
        assert.equal(await contract.read.bondholderWhitelist([bondId, user.account.address]), true);

        await contract.write.whitelistEquityholder([equityId, user.account.address]);
        assert.equal(await contract.read.equityholderWhitelist([equityId, user.account.address]), true);
    });

    it("Should issue bonds to a whitelisted recipient", async function () {
        const contract = await deployContract();

        // Setup
        await contract.write.createConvertibleBond([
            "Class A", "Meta", parseEther("1"), BigInt(Math.floor(Date.now() / 1000) + 10000),
            parseEther("100"), "SYMB", "Name", "ISIN", 500n
        ]);
        const bondId = 1n;
        await contract.write.whitelistBondholder([bondId, user.account.address]);

        const amount = 10n;
        await contract.write.issueBonds([bondId, user.account.address, amount]);

        assert.equal(await contract.read.balanceOf([user.account.address, bondId]), amount);

        const series = await contract.read.getBondSeries([bondId]);
        assert.equal(series.totalIssued, amount);
    });

    it("Should convert bonds to equity", async function () {
        const contract = await deployContract();

        // Setup
        const conversionRatio = parseEther("2"); // 1 bond = 2 equity
        await contract.write.createConvertibleBond([
            "Class A", "Meta", conversionRatio, BigInt(Math.floor(Date.now() / 1000) + 10000),
            parseEther("100"), "SYMB", "Name", "ISIN", 500n
        ]);
        const bondId = 1n;
        const equityId = 1000n;

        // Use a specific user wallet to test the conversion
        const userContract = await viem.getContractAt("ConvertibleBondTokenCRE", contract.address, { client: { wallet: user } });

        // Whitelist and issue bonds to user
        await contract.write.whitelistBondholder([bondId, user.account.address]);
        await contract.write.whitelistEquityholder([equityId, user.account.address]);
        await contract.write.issueBonds([bondId, user.account.address, 10n]);

        // Convert
        await userContract.write.convertBondsToEquity([bondId, 5n]);

        assert.equal(await contract.read.balanceOf([user.account.address, bondId]), 5n);
        assert.equal(await contract.read.balanceOf([user.account.address, equityId]), 10n); // 5 * 2 = 10

        const series = await contract.read.getBondSeries([bondId]);
        assert.equal(series.totalConverted, 5n);
    });

    it("Should handle CRE ISSUES_BONDS report", async function () {
        const contract = await deployContract();

        // Setup
        await contract.write.createConvertibleBond([
            "Class A", "Meta", parseEther("1"), BigInt(Math.floor(Date.now() / 1000) + 10000),
            parseEther("100"), "SYMB", "Name", "ISIN", 500n
        ]);
        const bondId = 1n;
        await contract.write.whitelistBondholder([bondId, user.account.address]);

        // CRE Operation ISSUE_BONDS is 0 (first in enum)
        // _handleCREIssueBonds decodes (uint256 bondId, address recipient, uint256 amount)

        // CREOperationType enum: ISSUE_BONDS = 0
        const operationType = 0;
        const operationData = encodeAbiParameters(
            [{ type: "uint256" }, { type: "address" }, { type: "uint256" }],
            [bondId, user.account.address, 20n]
        );

        const report = encodeAbiParameters(
            [{ type: "uint8" }, { type: "bytes" }],
            [operationType, operationData]
        );

        // Only forwarder can call onReport
        const forwarderContract = await viem.getContractAt("ConvertibleBondTokenCRE", contract.address, { client: { wallet: forwarder } });

        await forwarderContract.write.onReport(["0x", report]);

        assert.equal(await contract.read.balanceOf([user.account.address, bondId]), 20n);
    });

    it("Should handle CRE CONVERT_BONDS report", async function () {
        const contract = await deployContract();

        // Setup
        const conversionRatio = parseEther("2");
        await contract.write.createConvertibleBond([
            "Class A", "Meta", conversionRatio, BigInt(Math.floor(Date.now() / 1000) + 10000),
            parseEther("100"), "SYMB", "Name", "ISIN", 500n
        ]);
        const bondId = 1n;
        const equityId = 1000n;

        await contract.write.whitelistBondholder([bondId, user.account.address]);
        await contract.write.whitelistEquityholder([equityId, user.account.address]);
        await contract.write.issueBonds([bondId, user.account.address, 10n]);


        // CREOperationType enum: CONVERT_BONDS = 1
        const operationType = 1;
        const operationData = encodeAbiParameters(
            [{ type: "address" }, { type: "uint256" }, { type: "uint256" }],
            [user.account.address, bondId, 5n]
        );

        const report = encodeAbiParameters(
            [{ type: "uint8" }, { type: "bytes" }],
            [operationType, operationData]
        );

        const forwarderContract = await viem.getContractAt("ConvertibleBondTokenCRE", contract.address, { client: { wallet: forwarder } });
        await forwarderContract.write.onReport(["0x", report]);

        assert.equal(await contract.read.balanceOf([user.account.address, bondId]), 5n);
        assert.equal(await contract.read.balanceOf([user.account.address, equityId]), 10n);
    });

    it("Should handle CRE WHITELIST_BONDHOLDER report", async function () {
        const contract = await deployContract();
        await contract.write.createConvertibleBond([
            "Class A", "Meta", parseEther("1"), BigInt(Math.floor(Date.now() / 1000) + 10000),
            parseEther("100"), "SYMB", "Name", "ISIN", 500n
        ]);
        const bondId = 1n;


        // CREOperationType enum: WHITELIST_BONDHOLDER = 2
        const operationType = 2;
        const operationData = encodeAbiParameters(
            [{ type: "uint256" }, { type: "address" }],
            [bondId, user.account.address]
        );

        const report = encodeAbiParameters(
            [{ type: "uint8" }, { type: "bytes" }],
            [operationType, operationData]
        );

        const forwarderContract = await viem.getContractAt("ConvertibleBondTokenCRE", contract.address, { client: { wallet: forwarder } });
        await forwarderContract.write.onReport(["0x", report]);

        assert.equal(await contract.read.bondholderWhitelist([bondId, user.account.address]), true);
    });

    it("Should handle CRE UPDATE_CONVERSION_STATUS report", async function () {
        const contract = await deployContract();
        await contract.write.createConvertibleBond([
            "Class A", "Meta", parseEther("1"), BigInt(Math.floor(Date.now() / 1000) + 10000),
            parseEther("100"), "SYMB", "Name", "ISIN", 500n
        ]);
        const bondId = 1n;


        // CREOperationType enum: UPDATE_CONVERSION_STATUS = 4
        const operationType = 4;
        const operationData = encodeAbiParameters(
            [{ type: "uint256" }, { type: "bool" }],
            [bondId, false]
        );

        const report = encodeAbiParameters(
            [{ type: "uint8" }, { type: "bytes" }],
            [operationType, operationData]
        );

        const forwarderContract = await viem.getContractAt("ConvertibleBondTokenCRE", contract.address, { client: { wallet: forwarder } });
        await forwarderContract.write.onReport(["0x", report]);

        const series = await contract.read.getBondSeries([bondId]);
        assert.equal(series.conversionEnabled, false);
    });

    it("Should fail if conversion is disabled", async function () {
        const contract = await deployContract();
        await contract.write.createConvertibleBond([
            "Class A", "Meta", parseEther("1"), BigInt(Math.floor(Date.now() / 1000) + 10000),
            parseEther("100"), "SYMB", "Name", "ISIN", 500n
        ]);
        const bondId = 1n;
        const equityId = 1000n;

        await contract.write.setConversionEnabled([bondId, false]);
        await contract.write.whitelistBondholder([bondId, user.account.address]);
        await contract.write.whitelistEquityholder([equityId, user.account.address]);
        await contract.write.issueBonds([bondId, user.account.address, 10n]);

        const userContract = await viem.getContractAt("ConvertibleBondTokenCRE", contract.address, { client: { wallet: user } });

        await assert.rejects(
            userContract.write.convertBondsToEquity([bondId, 5n]),
            /Conversion is disabled/
        );
    });
});
