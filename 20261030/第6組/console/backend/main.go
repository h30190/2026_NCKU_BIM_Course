package main

import (
	"flag"
	"fmt"
	"log"
	"net/http"

	_ "modernc.org/sqlite"
)

func main() {
	addr := flag.String("addr", "127.0.0.1:8080", "listen addr（只綁 loopback，避免跳防火牆；與 RFI 範例相同）")
	dbPath := flag.String("db", "./data/lebr.db", "sqlite file path（壞了刪掉會自動重建）")
	seedDir := flag.String("seed", "", "資料庫沒有任何案子時，從這個資料夾匯入 *.json 快照（教室 demo 用）")
	frontendOrigin := flag.String("cors", "http://localhost:5173", "允許的前端 origin（Vite 預設）")
	blccPath := flag.String("blcc", "../../LebrCarbon/src/LebrCarbon.Data/Resources/blcc.LEBR-2025.json", "B-LCC 資料庫（與外掛共用同一份）")
	mappingPath := flag.String("mapping", "../../MaterialMapping/data/mapping.json", "材料對照表（AI agent 辨識結果）")
	proposalsPath := flag.String("proposals", "../../MaterialMapping/data/proposals.json", "減碳建議（AI agent 用情境試算產生）")
	flag.Parse()

	db, err := OpenDB(*dbPath)
	if err != nil {
		log.Fatal(err)
	}
	defer db.Close()

	srv := NewServer(db)
	srv.Mapping = openMappingStore(*blccPath, *mappingPath)
	srv.Proposals = NewProposalStore(*proposalsPath)
	if *seedDir != "" {
		n, err := srv.SeedIfEmpty(*seedDir)
		if err != nil {
			log.Printf("匯入範例快照失敗：%v", err)
		} else if n > 0 {
			fmt.Printf("已從 %s 匯入 %d 筆範例快照\n", *seedDir, n)
		}
	}

	mux := http.NewServeMux()
	srv.routes(mux)

	fmt.Printf("LebrCarbon 控制台後端 listening on %s (db=%s)\n", *addr, *dbPath)
	log.Fatal(http.ListenAndServe(*addr, withCORS(mux, *frontendOrigin)))
}

func withCORS(next http.Handler, origin string) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", origin)
		w.Header().Set("Access-Control-Allow-Methods", "GET,POST,PATCH,OPTIONS")
		w.Header().Set("Access-Control-Allow-Headers", "Content-Type")
		if r.Method == http.MethodOptions {
			w.WriteHeader(http.StatusNoContent)
			return
		}
		next.ServeHTTP(w, r)
	})
}
