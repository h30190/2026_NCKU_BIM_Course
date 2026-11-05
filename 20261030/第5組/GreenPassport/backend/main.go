package main

import (
	"flag"
	"fmt"
	"log"
	"net/http"
)

func main() {
	addr := flag.String("addr", "127.0.0.1:8080", "listen addr（只綁 loopback；Revit 外掛直接呼叫，不是瀏覽器，不需要開 CORS）")
	dbPath := flag.String("db", "./data/temporalbim.db", "sqlite file path")
	flag.Parse()

	db, err := OpenDB(*dbPath)
	if err != nil {
		log.Fatal(err)
	}
	defer db.Close()

	srv := NewServer(db)
	mux := http.NewServeMux()
	srv.routes(mux)

	fmt.Printf("Temporal BIM backend listening on %s (db=%s)\n", *addr, *dbPath)
	log.Fatal(http.ListenAndServe(*addr, mux))
}
