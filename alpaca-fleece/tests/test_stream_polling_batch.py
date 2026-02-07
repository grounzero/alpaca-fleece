"""Tests for batch polling functionality in StreamPolling.

Tests cover:
- Batch sizing logic
- Batch request formation
- Error handling for batch failures
- Backwards compatibility (batch_size=1 works like before)
"""

import asyncio
import pytest
from datetime import datetime, timezone
from unittest.mock import AsyncMock, MagicMock, patch

from alpaca.data.timeframe import TimeFrame
from alpaca.data.requests import StockBarsRequest

from src.stream_polling import StreamPolling, PollingBar, batch_iter


class TestBatchIter:
    """Tests for the batch_iter utility function."""
    
    def test_batch_iter_exact_multiple(self):
        """Test batching when length is exact multiple of batch size."""
        items = [1, 2, 3, 4, 5, 6]
        batches = list(batch_iter(items, 3))
        assert batches == [[1, 2, 3], [4, 5, 6]]
    
    def test_batch_iter_with_remainder(self):
        """Test batching when length leaves remainder."""
        items = [1, 2, 3, 4, 5]
        batches = list(batch_iter(items, 2))
        assert batches == [[1, 2], [3, 4], [5]]
    
    def test_batch_iter_single_batch(self):
        """Test batching when everything fits in one batch."""
        items = [1, 2, 3]
        batches = list(batch_iter(items, 10))
        assert batches == [[1, 2, 3]]
    
    def test_batch_iter_empty(self):
        """Test batching empty list."""
        batches = list(batch_iter([], 5))
        assert batches == []
    
    def test_batch_iter_single_items(self):
        """Test batching with batch_size=1."""
        items = ['A', 'B', 'C']
        batches = list(batch_iter(items, 1))
        assert batches == [['A'], ['B'], ['C']]


class TestStreamPollingBatchInit:
    """Tests for StreamPolling initialisation with batch_size."""
    
    def test_default_batch_size(self):
        """Test default batch size is 25."""
        stream = StreamPolling("api_key", "secret_key")
        assert stream.batch_size == 25
    
    def test_custom_batch_size(self):
        """Test custom batch size is stored."""
        stream = StreamPolling("api_key", "secret_key", batch_size=50)
        assert stream.batch_size == 50
    
    def test_batch_size_1_for_backwards_compat(self):
        """Test batch_size=1 for sequential polling (backwards compatible)."""
        stream = StreamPolling("api_key", "secret_key", batch_size=1)
        assert stream.batch_size == 1


class TestStreamPollingBatchLogic:
    """Tests for batch polling logic."""
    
    @pytest.fixture
    def mock_stream(self):
        """Create a StreamPolling instance with mocked client."""
        with patch('src.stream_polling.StockHistoricalDataClient'):
            stream = StreamPolling("api_key", "secret_key", batch_size=3)
            stream.client = MagicMock()
            return stream
    
    @pytest.fixture
    def sample_bar(self):
        """Create a sample bar object."""
        bar = MagicMock()
        bar.timestamp = datetime(2024, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
        bar.open = 100.0
        bar.high = 101.0
        bar.low = 99.0
        bar.close = 100.5
        bar.volume = 1000
        bar.vwap = 100.2
        return bar
    
    @pytest.mark.asyncio
    async def test_poll_batch_makes_single_request(self, mock_stream, sample_bar):
        """Test that batch polling makes a single API request for multiple symbols."""
        symbols = ['AAPL', 'MSFT', 'GOOGL']
        
        # Mock BarSet-like response with .data attribute
        mock_response = MagicMock()
        mock_response.data = {
            'AAPL': [sample_bar],
            'MSFT': [sample_bar],
            'GOOGL': [sample_bar],
        }
        mock_stream.client.get_stock_bars.return_value = mock_response
        
        await mock_stream._poll_batch(symbols)
        
        # Should make exactly one API call
        mock_stream.client.get_stock_bars.assert_called_once()
        
        # Verify the request was made with all symbols
        call_args = mock_stream.client.get_stock_bars.call_args
        request = call_args[0][0]
        assert isinstance(request, StockBarsRequest)
        assert request.symbol_or_symbols == symbols
    
    @pytest.mark.asyncio
    async def test_poll_batch_processes_all_symbols(self, mock_stream, sample_bar):
        """Test that batch polling processes bars for all symbols."""
        symbols = ['AAPL', 'MSFT', 'GOOGL']
        
        # Mock BarSet-like response with .data attribute
        mock_response = MagicMock()
        mock_response.data = {
            'AAPL': [sample_bar],
            'MSFT': [sample_bar],
            'GOOGL': [sample_bar],
        }
        mock_stream.client.get_stock_bars.return_value = mock_response
        
        processed_symbols = []
        mock_stream.on_bar = AsyncMock(side_effect=lambda bar: processed_symbols.append(bar.symbol))
        
        await mock_stream._poll_batch(symbols)
        
        assert set(processed_symbols) == set(symbols)
        assert len(processed_symbols) == 3
    
    @pytest.mark.asyncio
    async def test_poll_batch_handles_partial_response(self, mock_stream, sample_bar):
        """Test handling when some symbols have no data."""
        symbols = ['AAPL', 'MSFT', 'EMPTY']
        
        # Mock BarSet-like response with .data attribute
        mock_response = MagicMock()
        mock_response.data = {
            'AAPL': [sample_bar],
            'MSFT': [sample_bar],
            'EMPTY': [],  # No data for this symbol
        }
        mock_stream.client.get_stock_bars.return_value = mock_response
        
        processed_symbols = []
        mock_stream.on_bar = AsyncMock(side_effect=lambda bar: processed_symbols.append(bar.symbol))
        
        await mock_stream._poll_batch(symbols)
        
        # Should only process symbols with data
        assert 'EMPTY' not in processed_symbols
        assert len(processed_symbols) == 2
    
    @pytest.mark.asyncio
    async def test_poll_batch_handles_missing_symbol(self, mock_stream, sample_bar):
        """Test handling when a symbol is missing from response entirely."""
        symbols = ['AAPL', 'MSFT', 'MISSING']
        
        # Response doesn't include 'MISSING' at all
        mock_response = MagicMock()
        mock_response.data = {
            'AAPL': [sample_bar],
            'MSFT': [sample_bar],
        }
        mock_stream.client.get_stock_bars.return_value = mock_response
        
        processed_symbols = []
        mock_stream.on_bar = AsyncMock(side_effect=lambda bar: processed_symbols.append(bar.symbol))
        
        # Should not raise error
        await mock_stream._poll_batch(symbols)
        
        assert 'MISSING' not in processed_symbols
        assert len(processed_symbols) == 2
    
    @pytest.mark.asyncio
    async def test_poll_batch_error_handling(self, mock_stream):
        """Test that batch errors don't crash the loop."""
        symbols = ['AAPL', 'MSFT']
        
        # Simulate API error
        mock_stream.client.get_stock_bars.side_effect = Exception("API Error")
        
        # Should not raise - error is logged and swallowed
        await mock_stream._poll_batch(symbols)
        
        mock_stream.client.get_stock_bars.assert_called_once()


class TestStreamPollingBackwardsCompatibility:
    """Tests for backwards compatibility with batch_size=1."""
    
    @pytest.fixture
    def mock_stream(self):
        """Create a StreamPolling instance with batch_size=1."""
        with patch('src.stream_polling.StockHistoricalDataClient'):
            stream = StreamPolling("api_key", "secret_key", batch_size=1)
            stream.client = MagicMock()
            return stream
    
    @pytest.fixture
    def sample_bar(self):
        """Create a sample bar object."""
        bar = MagicMock()
        bar.timestamp = datetime(2024, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
        bar.open = 100.0
        bar.high = 101.0
        bar.low = 99.0
        bar.close = 100.5
        bar.volume = 1000
        return bar
    
    @pytest.mark.asyncio
    async def test_legacy_poll_symbol_still_works(self, mock_stream, sample_bar):
        """Test that _poll_symbol still works for backwards compatibility."""
        mock_response = MagicMock()
        mock_response.data = {'AAPL': [sample_bar]}
        mock_stream.client.get_stock_bars.return_value = mock_response
        
        processed = []
        mock_stream.on_bar = AsyncMock(side_effect=lambda bar: processed.append(bar.symbol))
        
        await mock_stream._poll_symbol('AAPL')
        
        assert processed == ['AAPL']
    
    @pytest.mark.asyncio
    async def test_batch_size_1_creates_individual_requests(self, mock_stream, sample_bar):
        """Test that batch_size=1 still creates individual requests per symbol."""
        mock_stream._symbols = ['AAPL', 'MSFT', 'GOOGL']
        
        mock_response = MagicMock()
        mock_response.data = {
            'AAPL': [sample_bar],
            'MSFT': [sample_bar],
            'GOOGL': [sample_bar],
        }
        mock_stream.client.get_stock_bars.return_value = mock_response
        
        # Simulate polling all symbols with batch_size=1
        for symbol in mock_stream._symbols:
            await mock_stream._poll_batch([symbol])
        
        # Should make 3 separate API calls
        assert mock_stream.client.get_stock_bars.call_count == 3


class TestStreamPollingBatchSizes:
    """Tests for various batch sizes and symbol counts."""
    
    @pytest.mark.asyncio
    async def test_31_symbols_with_batch_25(self):
        """Test that 31 symbols are split into 2 batches with batch_size=25."""
        with patch('src.stream_polling.StockHistoricalDataClient'):
            stream = StreamPolling("api_key", "secret_key", batch_size=25)
            stream.client = MagicMock()
            
            symbols = [f"SYM{i}" for i in range(31)]
            stream._symbols = symbols
            
            # Track number of batch calls
            batch_calls = []
            original_poll_batch = stream._poll_batch
            
            async def tracking_poll_batch(batch):
                batch_calls.append(batch)
                # Return empty to avoid processing
                return None
            
            stream._poll_batch = tracking_poll_batch
            
            # Simulate one iteration of poll loop
            for batch in batch_iter(symbols, stream.batch_size):
                await stream._poll_batch(batch)
            
            # Should have 2 batches: 25 + 6
            assert len(batch_calls) == 2
            assert len(batch_calls[0]) == 25
            assert len(batch_calls[1]) == 6
    
    @pytest.mark.asyncio
    async def test_500_symbols_with_batch_25(self):
        """Test that 500 symbols are split into 20 batches with batch_size=25."""
        with patch('src.stream_polling.StockHistoricalDataClient'):
            stream = StreamPolling("api_key", "secret_key", batch_size=25)
            
            symbols = [f"SYM{i}" for i in range(500)]
            
            batches = list(batch_iter(symbols, stream.batch_size))
            
            assert len(batches) == 20
            assert all(len(batch) == 25 for batch in batches[:-1])
            assert len(batches[-1]) == 25  # 500 is divisible by 25
    

class TestStreamPollingEfficiency:
    """Tests for efficiency gains from batch polling."""
    
    def test_api_call_reduction(self):
        """Test that batch polling reduces API calls."""
        # 31 symbols with batch_size=25 = 2 API calls instead of 31
        symbols = [f"SYM{i}" for i in range(31)]
        batch_size = 25
        
        batches = list(batch_iter(symbols, batch_size))
        num_api_calls = len(batches)
        
        assert num_api_calls == 2  # vs 31 without batching
        
        # 100 symbols = 4 API calls
        symbols = [f"SYM{i}" for i in range(100)]
        batches = list(batch_iter(symbols, batch_size))
        assert len(batches) == 4
    
    def test_efficiency_with_various_counts(self):
        """Test efficiency across different symbol counts."""
        test_cases = [
            (31, 25, 2),    # 31 symbols -> 2 batches
            (50, 25, 2),    # 50 symbols -> 2 batches
            (100, 25, 4),   # 100 symbols -> 4 batches
            (200, 25, 8),   # 200 symbols -> 8 batches
            (500, 25, 20),  # 500 symbols -> 20 batches
        ]
        
        for num_symbols, batch_size, expected_batches in test_cases:
            symbols = [f"SYM{i}" for i in range(num_symbols)]
            batches = list(batch_iter(symbols, batch_size))
            assert len(batches) == expected_batches, \
                f"Expected {expected_batches} batches for {num_symbols} symbols, got {len(batches)}"
