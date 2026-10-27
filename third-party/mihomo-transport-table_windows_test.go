package process

import (
	"errors"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func TestIPv6LensTransportTableSerial(t *testing.T) {
	const requests = 32
	var active, peak, reads atomic.Int32
	var workers sync.WaitGroup
	start := make(chan struct{})
	for i := 0; i < requests; i++ {
		workers.Add(1)
		go func() {
			defer workers.Done()
			<-start
			buf, err := serialTransportTableRead(func() ([]byte, error) {
				n := active.Add(1)
				for old := peak.Load(); n > old && !peak.CompareAndSwap(old, n); old = peak.Load() {
				}
				id := reads.Add(1)
				time.Sleep(time.Millisecond)
				active.Add(-1)
				return []byte{byte(id)}, nil
			})
			if err != nil || len(buf) != 1 || buf[0] == 0 {
				t.Errorf("fresh table read failed: %v %v", buf, err)
			}
		}()
	}
	close(start)
	workers.Wait()
	if peak.Load() != 1 || reads.Load() != requests {
		t.Fatalf("peak=%d reads=%d: must serialize without caching", peak.Load(), reads.Load())
	}
}

func TestIPv6LensTransportTableRelease(t *testing.T) {
	want := errors.New("fixture syscall failure")
	buf, err := serialTransportTableRead(func() ([]byte, error) { return nil, want })
	if buf != nil || err != want {
		t.Fatalf("error must propagate unchanged: %v %v", buf, err)
	}
	func() {
		defer func() {
			if recover() == nil {
				t.Error("panic must propagate")
			}
		}()
		_, _ = serialTransportTableRead(func() ([]byte, error) { panic("fixture panic") })
	}()
	done := make(chan struct{})
	go func() {
		defer close(done)
		buf, err := serialTransportTableRead(func() ([]byte, error) { return []byte{42}, nil })
		if err != nil || len(buf) != 1 || buf[0] != 42 {
			t.Errorf("read after error/panic: %v %v", buf, err)
		}
	}()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("table gate was not released")
	}
}
