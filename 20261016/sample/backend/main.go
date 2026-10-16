package main

import (
	"flag"
	"fmt"
	"log"
	"net/http"

	_ "modernc.org/sqlite"
)

func main() {
	addr := flag.String("addr", "127.0.0.1:8080", "listen addr（只綁 loopback，避免跳防火牆新增規則；教室要多人連同一台才改成 :8080）")
	dbPath := flag.String("db", "./data/rfi.db", "sqlite file path")
	dataDir := flag.String("data", "./data", "data dir (screenshots/, models/)")
	frontendOrigin := flag.String("cors", "http://localhost:5173", "允許的前端 origin（Vite 預設）")
	flag.Parse()

	db, err := OpenDB(*dbPath)
	if err != nil {
		log.Fatal(err)
	}
	defer db.Close()
	SeedAdmin(db)

	srv := NewServer(db, *dataDir)
	mux := http.NewServeMux()
	srv.routes(mux)

	handler := withCORS(mux, *frontendOrigin)
	fmt.Printf("RFI board listening on %s (db=%s)\n", *addr, *dbPath)
	log.Fatal(http.ListenAndServe(*addr, handler))
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
